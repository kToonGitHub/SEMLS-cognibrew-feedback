using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Moq;
using System.Net.Http.Json;
using System.Net;
using FluentAssertions;
using FeedbackService.Controllers;
using FeedbackService.Models;

namespace FeedbackService.Tests.Sociable;

public class FeedbackApiSociableTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FeedbackApiSociableTests(WebApplicationFactory<Program> factory)
    {
        // เราจะแอบเปลี่ยน IMongoCollection ตัวจริง ให้กลายเป็น Mock
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // ลบของเดิมออก
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoCollection<DailyFeedbackDocument>));
                if (descriptor != null) services.Remove(descriptor);

                // ใส่ Mock เข้าไปแทน
                var mockCollection = new Mock<IMongoCollection<DailyFeedbackDocument>>();
                mockCollection.Setup(c => c.UpdateOneAsync(It.IsAny<FilterDefinition<DailyFeedbackDocument>>(), It.IsAny<UpdateDefinition<DailyFeedbackDocument>>(), null, default))
                              .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

                services.AddSingleton(mockCollection.Object);

                // (ถ้ามี Background Service ที่ไม่อยากให้รันกวนตอนเทสต์ API ก็ Remove ออกได้เช่นกัน)
                var bgService = services.SingleOrDefault(d => d.ImplementationType == typeof(FeedbackService.Services.FeedbackBatchSenderService));
                if (bgService != null) services.Remove(bgService);
            });
        });
    }

    [Fact]
    public async Task PutFeedback_WithValidData_ReturnsOk()
    {
        // Arrange
        var client = _factory.CreateClient();
        var payload = new FeedbackUpdateRequest(true);

        // Act (ทดสอบยิง HTTP Request จริงๆ ไปที่ระบบจำลอง)
        var response = await client.PutAsJsonAsync("/api/v1/feedback/edge-001/2026-03-23/vec-123", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}