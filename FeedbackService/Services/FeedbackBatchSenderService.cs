using FeedbackService.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;
using System.Text.Json;

namespace FeedbackService.Services
{
    public class FeedbackBatchSenderService : BackgroundService
    {
        private readonly IMongoCollection<DailyFeedbackDocument> _collection;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<FeedbackBatchSenderService> _logger;

        public FeedbackBatchSenderService(
            IMongoCollection<DailyFeedbackDocument> collection,
            IHttpClientFactory httpClientFactory,
            IConfiguration config,
            ILogger<FeedbackBatchSenderService> logger)
        {
            _collection = collection;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ดึงระยะเวลาหน่วงจาก Config (ตั้งเป็นนาที) ถ้าไม่มีค่าเริ่มต้นที่ 60 นาที
            int intervalMinutes = _config.GetValue("SYNC_INTERVAL_MINUTES", 60);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessBatchAsync(stoppingToken);
            }
        }

        public async Task ProcessBatchAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting feedback sweep task...");
            var gatewayUrl = _config.GetValue<string>("GATEWAY_API_URL") ?? "http://localhost:8000/api/v1/gateway/batch";

            try
            {
                // เงื่อนไข: หา Document ที่มี Vector อย่างน้อย 1 ตัวที่ (ยังไม่เคย Sync) และ (Frontend ตอบแล้วว่าถูก/ผิด)
                var filter = Builders<DailyFeedbackDocument>.Filter.ElemMatch(x => x.Vectors,
                    v => v.IsSynced == false && v.IsCorrect != null);

                var pendingDocs = await _collection.Find(filter).ToListAsync(stoppingToken);

                var httpClient = _httpClientFactory.CreateClient();

                foreach (var doc in pendingDocs)
                {
                    // กรองเอาเฉพาะข้อมูลข้างในที่พร้อมส่งจริงๆ
                    var vectorsToSend = doc.Vectors
                        .Where(v => !v.IsSynced && v.IsCorrect.HasValue)
                        .ToList();

                    if (!vectorsToSend.Any()) continue;

                    // 1. จัดเตรียม Payload ตามหน้าตาที่ปลายทางต้องการ
                    var payload = new
                    {
                        device_id = doc.DeviceId,
                        date = doc.Date,
                        vectors = vectorsToSend.Select(v => new
                        {
                            username = v.Username,
                            embedding = v.Embedding,
                            is_correct = v.IsCorrect!.Value
                        })
                    };

                    var jsonPayload = JsonSerializer.Serialize(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // 2. ยิง API ส่ง Batch ไปที่ Gateway
                    var response = await httpClient.PostAsync(gatewayUrl, content, stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // 3. ท่าไม้ตาย MongoDB: อัปเดต Array เฉพาะ Element ที่เพิ่งส่งไปให้ IsSynced = true
                        var vectorIdsToUpdate = vectorsToSend.Select(v => v.VectorId).ToList();

                        var updateFilter = Builders<DailyFeedbackDocument>.Filter.Eq(x => x.Id, doc.Id);
                        var updateAction = Builders<DailyFeedbackDocument>.Update.Set("vectors.$[elem].is_synced", true);

                        // ใช้ ArrayFilters เพื่อจับคู่หาตัวที่ต้องอัปเดต
                        var arrayFilters = new List<ArrayFilterDefinition>
                        {
                            new BsonDocumentArrayFilterDefinition<BsonDocument>(
                                new BsonDocument("elem.vector_id", new BsonDocument("$in", new BsonArray(vectorIdsToUpdate))))
                        };

                        var updateOptions = new UpdateOptions { ArrayFilters = arrayFilters };

                        await _collection.UpdateOneAsync(updateFilter, updateAction, updateOptions, stoppingToken);

                        _logger.LogInformation($"Successfully synced {vectorsToSend.Count} records for device {doc.DeviceId} on {doc.Date}.");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to sync for {doc.DeviceId}. Status: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during feedback sweep task.");
            }
        }
    }
}
