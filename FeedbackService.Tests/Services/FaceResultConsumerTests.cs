using Cognibrew.Events; // สำหรับใช้งาน FaceResult
using FeedbackService.Models;
using FeedbackService.Services;
using FluentAssertions;
using Google.Protobuf; // สำหรับคำสั่ง ToByteArray()
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using Testcontainers.MongoDb;

namespace FeedbackService.Tests.Services;

public class FaceResultConsumerTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;
    private IMongoCollection<DailyFeedbackDocument> _collection = null!;
    private FaceResultConsumerService _service = null!;

    [Obsolete]
    public FaceResultConsumerTests()
    {
        _mongoContainer = new MongoDbBuilder().WithImage("mongo:latest").Build();
    }

    public async Task InitializeAsync()
    {
        await _mongoContainer.StartAsync();
        var client = new MongoClient(_mongoContainer.GetConnectionString());
        //var client = new MongoClient("mongodb://root:example@localhost:27017");
        var db = client.GetDatabase("TestDb");
        _collection = db.GetCollection<DailyFeedbackDocument>("Feedbacks");

        // จำลอง Configuration (ให้ตรงกับโค้ดที่ดึง DEVICE_ID ไปใช้)
        var settings = new Dictionary<string, string> { { "DEVICE_ID", "test-edge-001" } };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var mockLogger = new Mock<ILogger<FaceResultConsumerService>>();

        // สร้าง Service ของเราขึ้นมา
        _service = new FaceResultConsumerService(_collection, config, mockLogger.Object);
    }

    public async Task DisposeAsync() => await _mongoContainer.DisposeAsync();
    //public Task DisposeAsync()
    //{
    //    // await _mongoContainer.DisposeAsync();
    //    return Task.CompletedTask;
    //}

    [Fact]
    public async Task ProcessMessageAsync_FirstFaceOfDay_ShouldCreateNewDocument()
    {
        // 1. Arrange: สร้างข้อมูล Protobuf จำลอง
        var mockFaceResult = new FaceResult
        {
            FaceId = "face-001",
            Username = "Alice",
            Score = 0.95f,
            Embedding = { 0.1f, 0.2f, 0.3f } // ลองใส่ Vector เล็กๆ
        };

        // แปลง Object ให้กลายเป็น Byte Array ดิบๆ เหมือนที่ RabbitMQ จะส่งมาให้
        byte[] payload = mockFaceResult.ToByteArray();

        // 2. Act: เรียกใช้ฟังก์ชันประมวลผล
        await _service.ProcessMessageAsync(payload);

        // 3. Assert: ตรวจสอบผลลัพธ์ใน MongoDB ว่าต้องมี Document ใหม่โผล่ขึ้นมา
        string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
        var docs = await _collection.Find(x => x.DeviceId == "test-edge-001" && x.Date == currentDate).ToListAsync();

        docs.Should().HaveCount(1); // ต้องมี 1 Document
        docs[0].Vectors.Should().HaveCount(1); // ข้างใน Array ต้องมี 1 หน้า

        var savedVector = docs[0].Vectors.First();
        savedVector.VectorId.Should().Be("face-001");
        savedVector.Username.Should().Be("Alice");
        savedVector.IsCorrect.Should().BeNull(); // สถานะเริ่มต้นต้องเป็น Null รอ User มากด
        savedVector.IsSynced.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessMessageAsync_SecondFaceOfDay_ShouldPushToExistingArray()
    {
        // 1. Arrange: แอบเอา Document ของวันนี้ยัดลง Database ไปก่อน (จำลองว่ามีข้อมูลแล้ว 1 หน้า)
        string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
        await _collection.InsertOneAsync(new DailyFeedbackDocument
        {
            DeviceId = "test-edge-001",
            Date = currentDate,
            Vectors = new List<FeedbackVector>
            {
                new FeedbackVector { VectorId = "face-old", Username = "Bob" }
            }
        });

        // สร้างข้อมูลหน้าคนที่ 2 ที่เข้ามาใหม่
        var newFaceResult = new FaceResult
        {
            FaceId = "face-new",
            Username = "Charlie",
            Score = 0.88f,
            Embedding = { 0.9f, 0.8f }
        };
        byte[] payload = newFaceResult.ToByteArray();

        // 2. Act
        await _service.ProcessMessageAsync(payload);

        // 3. Assert: ต้องไม่สร้าง Document ใหม่ แต่ต้องยัดลง Array เดิม
        var docs = await _collection.Find(x => x.DeviceId == "test-edge-001" && x.Date == currentDate).ToListAsync();

        docs.Should().HaveCount(1); // Document วันนี้ยังคงมี 1 อันเหมือนเดิม
        docs[0].Vectors.Should().HaveCount(2); // แต่หน้าใน Array ต้องเพิ่มเป็น 2 หน้า

        // ตรวจสอบว่าหน้าล่าสุดที่เข้าไปคือ Charlie จริงๆ
        var lastVector = docs[0].Vectors.Last();
        lastVector.VectorId.Should().Be("face-new");
        lastVector.Username.Should().Be("Charlie");
    }

    [Fact]
    public async Task ProcessMessageAsync_InvalidProtobufData_ShouldThrowException()
    {
        // 1. Arrange: สร้าง Byte ขยะที่ไม่มีทางถอดรหัสเป็น Protobuf ได้
        byte[] invalidPayload = new byte[] { 0xFF, 0xAA, 0xBB, 0xCC };

        // 2. Act & Assert: เมื่อเรียกใช้งาน ต้องเกิด Error (InvalidProtocolBufferException)
        Func<Task> act = async () => await _service.ProcessMessageAsync(invalidPayload);

        await act.Should().ThrowAsync<InvalidProtocolBufferException>();
    }

    [Fact]
    public async Task ProcessMessageAsync_ConcurrentRequests_ShouldNotLoseDataAndCreateOnlyOneDocument()
    {
        // 1. Arrange: เตรียมข้อมูลตั้งต้น
        int concurrentCount = 500; // จำลองว่าส่งหน้าเข้ามาพร้อมกันเป๊ะ 500 หน้า
        var tasks = new List<Task>();
        string currentDate = DateTime.Now.ToString("yyyy-MM-dd");

        // ล้างข้อมูลเก่าออกก่อน (เพื่อให้เป็นจังหวะ "สร้างใหม่" จริงๆ)
        await _collection.DeleteManyAsync(Builders<DailyFeedbackDocument>.Filter.Empty);

        // 2. Act: สร้าง Loop เพื่อจำลองคนส่งข้อความมาถล่มพร้อมกัน
        for (int i = 0; i < concurrentCount; i++)
        {
            var mockFace = new FaceResult
            {
                FaceId = $"face-concurrent-{i}",
                Username = $"User_{i}",
                Score = 0.9f,
                Embedding = { 0.1f, 0.2f }
            };
            byte[] payload = mockFace.ToByteArray();

            // Task.Run เป็นการบังคับให้ไปทำงานใน Thread อื่นทันที โดยไม่รอให้เสร็จ
            tasks.Add(Task.Run(() => _service.ProcessMessageAsync(payload)));
        }

        // รอให้ทั้ง 500 Thread ทำงานเสร็จพร้อมกัน
        await Task.WhenAll(tasks);

        // 3. Assert: ตรวจสอบผลลัพธ์
        var docs = await _collection.Find(x => x.DeviceId == "test-edge-001" && x.Date == currentDate).ToListAsync();

        // ตรวจสอบที่ 1: ต้องไม่มี Document ขยะงอกออกมา ต้องมีแค่ 1 Document ของวันนี้เท่านั้น
        docs.Should().HaveCount(1);

        // ตรวจสอบที่ 2: ข้อมูลทั้ง 50 หน้า ต้องถูกยัดลง Array ได้ครบถ้วน ไม่มีหน้าไหนหล่นหายจากการโดนทับ
        docs[0].Vectors.Should().HaveCount(concurrentCount);
    }
}