using FeedbackService.Models; // ปรับ Namespace ให้ตรงกับของคุณ
using FeedbackService.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using Moq.Protected;
using System.Net;
using Testcontainers.MongoDb;

namespace FeedbackService.Tests.Services;

public class FeedbackBatchSenderTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;
    private IMongoCollection<DailyFeedbackDocument> _collection = null!;

    public FeedbackBatchSenderTests()
    {
        _mongoContainer = new MongoDbBuilder().WithImage("mongo:latest").Build();
    }

    public async Task InitializeAsync()
    {
        await _mongoContainer.StartAsync();
        var client = new MongoClient(_mongoContainer.GetConnectionString());
        var db = client.GetDatabase("TestDb");
        _collection = db.GetCollection<DailyFeedbackDocument>("Feedbacks");
    }

    public async Task DisposeAsync() => await _mongoContainer.DisposeAsync();

    // ตัวช่วยสร้าง HttpClient จำลอง (จำลองว่า Gateway ตอบกลับมาเป็นอะไร)
    private IHttpClientFactory CreateMockHttpClientFactory(HttpStatusCode statusCodeToReturn)
    {
        var handlerMock = new Mock<HttpMessageHandler>();

        // 💡 ทริค: HttpClient ไม่มี Interface ให้ Mock ตรงๆ เราต้อง Mock ผ่าน Protected Method ของ HttpMessageHandler
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage { StatusCode = statusCodeToReturn });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return factoryMock.Object;
    }

    // ตัวช่วยสร้าง IConfiguration จำลอง
    private IConfiguration CreateInMemoryConfig()
    {
        var settings = new Dictionary<string, string> { { "GATEWAY_API_URL", "http://fake-gateway" } };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenGatewayReturns200_ShouldUpdateIsSyncedToTrue()
    {
        // 1. Arrange: เตรียมข้อมูลตั้งต้นใน MongoDB แบบของจริง
        var doc = new DailyFeedbackDocument
        {
            DeviceId = "test-device",
            Date = "2026-03-23",
            Vectors = new List<FeedbackVector>
            {
                // เคสนี้ตรงเงื่อนไข: ถูก Feedback แล้ว (IsCorrect = true) และยังไม่ได้ส่ง (IsSynced = false)
                new FeedbackVector { VectorId = "vec-1", IsCorrect = true, IsSynced = false }
            }
        };
        await _collection.InsertOneAsync(doc);

        // จำลองให้ Gateway ตอบ 200 OK
        var mockHttpFactory = CreateMockHttpClientFactory(HttpStatusCode.OK);
        var config = CreateInMemoryConfig();
        var mockLogger = new Mock<ILogger<FeedbackBatchSenderService>>(); // โค้ดโปรเจกต์คุณอาจไม่มี namespace นี้ ให้แก้ชื่อคลาสเป็นของโปรเจกต์จริง

        var service = new FeedbackBatchSenderService(_collection, mockHttpFactory, config, mockLogger.Object);

        // 2. Act: สั่งกวาดและส่ง
        await service.ProcessBatchAsync(CancellationToken.None);

        // 3. Assert: เช็คข้อมูลใน DB ว่า IsSynced ต้องกลายเป็น true แล้ว
        var updatedDoc = await _collection.Find(x => x.DeviceId == "test-device").FirstOrDefaultAsync();
        updatedDoc.Vectors.First().IsSynced.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenGatewayReturns500_ShouldNotUpdateIsSynced()
    {
        // 1. Arrange
        var doc = new DailyFeedbackDocument
        {
            DeviceId = "test-device",
            Date = "2026-03-23",
            Vectors = new List<FeedbackVector>
            {
                new FeedbackVector { VectorId = "vec-fail", IsCorrect = false, IsSynced = false }
            }
        };
        await _collection.InsertOneAsync(doc);

        // จำลองให้ Gateway ล่ม ตอบ 500 Internal Server Error
        var mockHttpFactory = CreateMockHttpClientFactory(HttpStatusCode.InternalServerError);
        var config = CreateInMemoryConfig();
        var mockLogger = new Mock<ILogger<FeedbackBatchSenderService>>();

        var service = new FeedbackBatchSenderService(_collection, mockHttpFactory, config, mockLogger.Object);

        // 2. Act
        await service.ProcessBatchAsync(CancellationToken.None);

        // 3. Assert: ✨ จุดสำคัญ! ถ้าส่งไม่สำเร็จ IsSynced ต้องยังคงเป็น false เหมือนเดิม เพื่อให้รอบถัดไปมันหยิบมาส่งใหม่
        var updatedDoc = await _collection.Find(x => x.DeviceId == "test-device").FirstOrDefaultAsync();
        updatedDoc.Vectors.First().IsSynced.Should().BeFalse();
    }
}