namespace FeedbackService.Models
{
    using MongoDB.Bson;
    using MongoDB.Bson.Serialization.Attributes;

    public class DailyFeedbackDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("device_id")]
        public string DeviceId { get; set; } = string.Empty;

        [BsonElement("date")]
        public string Date { get; set; } = string.Empty; // รูปแบบ "yyyy-MM-dd"

        [BsonElement("vectors")]
        public List<FeedbackVector> Vectors { get; set; } = new();
    }

    public class FeedbackVector
    {
        // สร้าง ID เฉพาะให้แต่ละใบหน้า เพื่อให้ Frontend ชี้เป้าตอนแก้ is_correct ได้ง่าย
        [BsonElement("vector_id")]
        public string VectorId { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("username")]
        public string Username { get; set; } = string.Empty;

        [BsonElement("embedding")]
        public List<float> Embedding { get; set; } = new();

        // nullable (bool?) เพื่อบอกว่า Frontend ยังไม่ได้กด Feedback
        [BsonElement("is_correct")]
        public bool? IsCorrect { get; set; }

        [BsonElement("feedback")]
        public string Feedback { get; set; } = string.Empty;

        // ตัวแปรสำคัญ: เอาไว้เช็คว่า Background process ส่งข้อมูลนี้ไปหรือยัง
        [BsonElement("is_synced")]
        public bool IsSynced { get; set; } = false;
    }
}
