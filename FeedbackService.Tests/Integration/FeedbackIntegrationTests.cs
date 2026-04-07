using FeedbackService.Controllers;
using FeedbackService.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System.Net;
using System.Net.Http.Json;
using Testcontainers.MongoDb;

namespace FeedbackService.Tests.Integration;

// IAsyncLifetime ช่วยให้เรา Start Container ก่อนรันเทสต์ และ Stop หลังรันเสร็จได้
public class FeedbackIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [Obsolete]
    public FeedbackIntegrationTests()
    {
        // กำหนดสเปคว่าจะสร้าง MongoDB เวอร์ชั่นไหน
        _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:latest")
            .Build();
    }

    public async Task InitializeAsync()
    {
        // 1. สั่งรัน MongoDB Container
        await _mongoContainer.StartAsync();

        // 2. ผูก Connection String ของ Container จริงๆ เข้ากับ App ของเรา
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // เตะ MongoDB เดิมทิ้ง
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(IMongoCollection<DailyFeedbackDocument>) ||
                    d.ServiceType == typeof(MongoClient)).ToList();
                foreach (var d in descriptors) services.Remove(d);

                // เชื่อมกับ Testcontainer ที่เพิ่งสร้างมาสดๆ ร้อนๆ
                var client = new MongoClient(_mongoContainer.GetConnectionString());
                var db = client.GetDatabase("TestDb");
                var collection = db.GetCollection<DailyFeedbackDocument>("TestFeedbacks");

                services.AddSingleton(collection);
            });
        });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        // หลังเทสต์เสร็จ สั่งปิดและลบ Container ทิ้ง
        await _mongoContainer.DisposeAsync();
    }

    [Fact]
    public async Task Integration_UpdateFeedback_ActuallyUpdatesDatabase()
    {
        // Arrange 1: แอบเอาข้อมูลตั้งต้นยัดใส่ Database จริงๆ ก่อน
        var collection = _factory.Services.GetRequiredService<IMongoCollection<DailyFeedbackDocument>>();
        var doc = new DailyFeedbackDocument
        {
            DeviceId = "edge-001",
            Date = "2026-03-23",
            Vectors = new List<FeedbackVector>
            {
                new FeedbackVector { VectorId = "vec-real-01", IsCorrect = null, IsSynced = false }
            }
        };
        await collection.InsertOneAsync(doc);

        // Arrange 2: เตรียม Payload ยิง API
        var payload = new FeedbackUpdateRequest(false);

        // Act: ยิงผ่าน HTTP จริงๆ
        var response = await _client.PutAsJsonAsync("/api/v1/feedback/edge-001/2026-03-23/vec-real-01", payload);

        // Assert 1: HTTP ต้องตอบกลับสำเร็จ
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert 2: ข้อมูลใน Database ต้องถูกเปลี่ยนจริงๆ
        var updatedDoc = await collection.Find(x => x.DeviceId == "edge-001").FirstOrDefaultAsync();
        var targetVector = updatedDoc.Vectors.First(v => v.VectorId == "vec-real-01");

        targetVector.IsCorrect.Should().BeFalse(); // ข้อมูลโดนแก้ตามที่ยิงไปแล้ว!
    }
}