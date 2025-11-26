using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models; // הוסף את זה!

namespace TodoApi
{
    // Records
    public record RegisterRequest(string Username, string Password);
    public record LoginRequest(string Username, string Password);
    public record LoginResponse(string Token);

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // שירותים
            builder.Services.AddEndpointsApiExplorer();
            
            // Swagger עם תמיכה ב-JWT
            builder.Services.AddSwaggerGen(options =>
            {
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "Bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Enter your JWT token in the format: Bearer {your token}"
                });

                options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        new string[] {}
                    }
                });
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowReactApp", policy =>
                {
                    policy.WithOrigins("https://todolistreact-jazd.onrender.com")
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials(); 
                });
            });

            // DbContext
            builder.Services.AddDbContext<ToDoDbContext>(options =>
                options.UseMySql(builder.Configuration.GetConnectionString("ToDoDB"),
                    new MySqlServerVersion(new Version(8, 0, 44))));

            // JWT
            var jwtSection = builder.Configuration.GetSection("Jwt");
            var key = jwtSection["Key"] ?? throw new Exception("JWT Key is missing!");
            var issuer = jwtSection["Issuer"] ?? throw new Exception("JWT Issuer is missing!");
            var audience = jwtSection["Audience"] ?? throw new Exception("JWT Audience is missing!");
            var expireMinutes = int.Parse(jwtSection["ExpireMinutes"] ?? "60");

            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false; // שים לב - שיניתי לfalse למען הפיתוח
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ValidateLifetime = true
                };
            });

            builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
            builder.Services.AddAuthorization();

            var app = builder.Build();

            
            //if (app.Environment.IsDevelopment() || true)
            //{
            app.UseSwagger();
            app.UseSwaggerUI();
            //}

            app.UseCors("AllowReactApp");
            app.UseAuthentication();
            app.UseAuthorization();

            // Endpoints
            static int GetUserId(ClaimsPrincipal user)
            {
                var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return int.Parse(userIdClaim ?? "0");
            }

            
            app.MapGet("/tasks", async (ToDoDbContext context, ClaimsPrincipal user) =>
            {
                var userId = GetUserId(user);
                var tasks = await context.Items
                    .Where(t => t.UserId == userId)
                    .ToListAsync();
                return Results.Ok(tasks);
            }).RequireAuthorization();

           
            app.MapPost("/tasks", async (ToDoDbContext context, ClaimsPrincipal user, Item newTask) =>
            {
                var userId = GetUserId(user);
                
                newTask.UserId = userId;
                
                context.Items.Add(newTask);
                await context.SaveChangesAsync();
                return Results.Created($"/tasks/{newTask.Id}", newTask);
            }).RequireAuthorization();

            
            app.MapPut("/tasks/{id}", async (int id, ToDoDbContext context, ClaimsPrincipal user, Item updatedTask) =>
            {
                var userId = GetUserId(user);
                
                var task = await context.Items
                    .Where(t => t.Id == id && t.UserId == userId)
                    .FirstOrDefaultAsync();
                    
                if (task is null) 
                    return Results.NotFound("Task not found or you don't have permission");

                task.Name = updatedTask.Name;
                task.IsComplete = updatedTask.IsComplete;
                

                await context.SaveChangesAsync();
                return Results.Ok(task);
            }).RequireAuthorization();

            
            app.MapDelete("/tasks/{id}", async (int id, ToDoDbContext context, ClaimsPrincipal user) =>
            {
                var userId = GetUserId(user);
                
                var task = await context.Items
                    .Where(t => t.Id == id && t.UserId == userId)
                    .FirstOrDefaultAsync();
                    
                if (task is null) 
                    return Results.NotFound("Task not found or you don't have permission");

                context.Items.Remove(task);
                await context.SaveChangesAsync();
                return Results.NoContent();
            }).RequireAuthorization();

        
            app.MapPost("/auth/register", async (ToDoDbContext context, IPasswordHasher<User> hasher, RegisterRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                    return Results.BadRequest("Username and password required.");

                var exists = await context.Users.AnyAsync(u => u.Username == req.Username);
                if (exists) return Results.Conflict("Username already exists.");

                var user = new User { Username = req.Username };
                user.PasswordHash = hasher.HashPassword(user, req.Password);

                context.Users.Add(user);
                await context.SaveChangesAsync();

                return Results.Created($"/users/{user.Id}", new { user.Id, user.Username });
            });

            app.MapPost("/auth/login", async (ToDoDbContext context, IPasswordHasher<User> hasher, IConfiguration config, LoginRequest req) =>
            {
                var user = await context.Users.SingleOrDefaultAsync(u => u.Username == req.Username);
                if (user is null) return Results.Unauthorized();

                var res = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
                if (res == PasswordVerificationResult.Failed) return Results.Unauthorized();

                var jwtSection2 = config.GetSection("Jwt");
                var key2 = jwtSection2["Key"] ?? throw new Exception("JWT Key is missing!");
                var issuer2 = jwtSection2["Issuer"];
                var audience2 = jwtSection2["Audience"];
                var expireMinutes2 = int.Parse(jwtSection2["ExpireMinutes"] ?? "60");

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username)
                };

                var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var tokenKey = Encoding.UTF8.GetBytes(key2);
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = DateTime.UtcNow.AddMinutes(expireMinutes2),
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(tokenKey), SecurityAlgorithms.HmacSha256Signature),
                    Issuer = issuer2,
                    Audience = audience2
                };

                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);

                return Results.Ok(new { token = tokenString });
            });

            app.MapGet("/", () => "ToDoList Api is running now!");

            app.Run();
        }
    }
}