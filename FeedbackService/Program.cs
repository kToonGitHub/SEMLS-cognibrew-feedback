
using FeedbackService.Models;
using FeedbackService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text;

namespace FeedbackService
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            builder.Services.AddOpenApi();
            builder.Services.AddSwaggerGen(SetSwaggerOptions);
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options => SetJwtOptions(options, builder));

            // ตั้งค่า MongoDB
            var mongoClient = new MongoClient(builder.Configuration.GetConnectionString("MongoDb"));
            var database = mongoClient.GetDatabase("CognibrewDb");
            var feedbackCollection = database.GetCollection<DailyFeedbackDocument>("Feedbacks");

            builder.Services.AddSingleton(feedbackCollection);
            builder.Services.AddHttpClient();
            builder.Services.AddHostedService<FaceResultConsumerService>();
            builder.Services.AddHostedService<FeedbackBatchSenderService>(); // Background process ยังอยู่เหมือนเดิม

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            //app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();
            app.Run();
        }

        private static void SetJwtOptions(JwtBearerOptions options, WebApplicationBuilder builder)
        {
            (string issuer, string audience, string key) = GetJwtInfo(builder);
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters()
            {
                ValidateIssuer = true,
                ValidateActor = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            };
        }

        private static void SetSwaggerOptions(SwaggerGenOptions options)
        {
            options.SwaggerDoc("v1", new() { Title = "ReservationService API", Version = "v1" });
            options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                Description = "Enter your valid token.\nExample: \"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...\""
            });
            options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
            {
                {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference = new Microsoft.OpenApi.Models.OpenApiReference
                        {
                            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        }

        private static (string issuer, string audience, string key) GetJwtInfo(WebApplicationBuilder builder)
        {
            IConfigurationSection? jwtSettings = builder.Configuration.GetSection("Jwt");
            if (jwtSettings is null)
            {
                throw new ArgumentNullException(nameof(jwtSettings));
            }
            string? issuer = jwtSettings["Issuer"];
            string? audience = jwtSettings["Audience"];
            string? key = jwtSettings["Key"];
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(audience) || string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("issuer or audience or key");
            }
            return (issuer, audience, key);
        }
    }
}
