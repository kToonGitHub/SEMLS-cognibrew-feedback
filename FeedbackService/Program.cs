
using FeedbackService.Models;
using FeedbackService.Services;
using MongoDB.Driver;

namespace FeedbackService
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            builder.Services.AddOpenApi();

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
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();


            app.MapControllers();

            app.Run();
        }
    }
}
