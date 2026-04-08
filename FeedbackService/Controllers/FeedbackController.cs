using FeedbackService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace FeedbackService.Controllers;

[ApiController]
[Route("api/v1/[controller]")] // Route จะกลายเป็น /api/v1/feedback โดยอัตโนมัติ
public class FeedbackController : ControllerBase
{
    private readonly IMongoCollection<DailyFeedbackDocument> _collection;

    public FeedbackController(IMongoCollection<DailyFeedbackDocument> collection)
    {
        _collection = collection;
    }

    // PUT: api/v1/feedback/{deviceId}/{date}/{vectorId}
    [Authorize(Roles ="Admin,Owner,User")]
    [HttpPut("{deviceId}/{date}/{vectorId}")]
    public async Task<IActionResult> UpdateFeedback(string deviceId, string date, string vectorId, [FromBody] FeedbackUpdateRequest request)
    {
        // ค้นหา Document ของเครื่องและวันนั้นๆ และหา Vector ที่ตรงกับ vectorId
        var filter = Builders<DailyFeedbackDocument>.Filter.Eq(x => x.DeviceId, deviceId) &
                     Builders<DailyFeedbackDocument>.Filter.Eq(x => x.Date, date) &
                     Builders<DailyFeedbackDocument>.Filter.ElemMatch(x => x.Vectors, v => v.VectorId == vectorId);

        // อัปเดตเฉพาะฟิลด์ is_correct ใน Array
        var update = Builders<DailyFeedbackDocument>.Update.Set("vectors.$.is_correct", request.IsCorrect);

        var result = await _collection.UpdateOneAsync(filter, update);

        if (result.ModifiedCount > 0)
        {
            return Ok(new { message = "Feedback updated successfully." });
        }

        // กรณีที่ไม่เจอข้อมูลที่ต้องอัปเดต
        return NotFound(new { message = "Vector not found or no changes made." });
    }
}

// ย้าย DTO มาไว้ในไฟล์เดียวกัน
public record FeedbackUpdateRequest(bool IsCorrect);