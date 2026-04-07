using FeedbackService.Controllers;
using FeedbackService.Models; // ถ้าเอา Model ไว้โฟลเดอร์อื่น ให้ปรับ Namespace ด้วยครับ
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Moq;

namespace FeedbackService.Tests.Controllers;

public class FeedbackControllerTests
{
    private readonly Mock<IMongoCollection<DailyFeedbackDocument>> _mockCollection;
    private readonly FeedbackController _controller;

    public FeedbackControllerTests()
    {
        // 1. จำลอง (Mock) MongoDB Collection
        _mockCollection = new Mock<IMongoCollection<DailyFeedbackDocument>>();

        // 2. เอาของจำลองยัดใส่ Controller
        _controller = new FeedbackController(_mockCollection.Object);
    }

    [Fact]
    public async Task UpdateFeedback_ShouldReturnOk_WhenUpdateIsSuccessful()
    {
        // Arrange: เตรียมข้อมูลและจำลองพฤติกรรม
        var request = new FeedbackUpdateRequest(true);

        // จำลองผลลัพธ์ของ MongoDB ให้บอกว่า "แก้ไขสำเร็จ 1 แถว"
        var updateResult = new UpdateResult.Acknowledged(1, 1, null);

        // Setup ให้ตอนที่ Controller เรียก UpdateOneAsync ให้คืนค่าผลลัพธ์ที่เราจำลองไว้
        _mockCollection.Setup(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<DailyFeedbackDocument>>(),
            It.IsAny<UpdateDefinition<DailyFeedbackDocument>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);

        // Act: ลงมือเรียกใช้งาน API
        var result = await _controller.UpdateFeedback("edge-001", "2026-03-23", "vec-123", request);

        // Assert: ตรวจสอบว่าผลลัพธ์ต้องเป็น 200 OK
        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task UpdateFeedback_ShouldReturnNotFound_WhenNoRecordUpdated()
    {
        // Arrange
        var request = new FeedbackUpdateRequest(false);
        var updateResult = new UpdateResult.Acknowledged(1, 0, null); // ModifiedCount = 0 (หาไม่เจอ)

        _mockCollection.Setup(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<DailyFeedbackDocument>>(),
            It.IsAny<UpdateDefinition<DailyFeedbackDocument>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);

        // Act
        var result = await _controller.UpdateFeedback("edge-001", "2026-03-23", "vec-999", request);

        // Assert: ต้องได้ 404 Not Found
        var notFoundResult = result as NotFoundObjectResult;
        notFoundResult.Should().NotBeNull();
        notFoundResult!.StatusCode.Should().Be(404);
    }
}