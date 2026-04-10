using Cognibrew.Events; // Namespace ของ Protobuf
using FeedbackService.Models;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FeedbackService.Services;

public class FaceResultConsumerService : BackgroundService
{
    private readonly IMongoCollection<DailyFeedbackDocument> _collection;
    private readonly IConfiguration _config;
    private readonly ILogger<FaceResultConsumerService> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public FaceResultConsumerService(
        IMongoCollection<DailyFeedbackDocument> collection,
        IConfiguration config,
        ILogger<FaceResultConsumerService> logger)
    {
        _collection = collection;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:HostName"] ?? "localhost",
            UserName = _config["RabbitMQ:UserName"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest" 
        };

        try
        {
            // เชื่อมต่อ RabbitMQ
            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

            string queueName = "cognibrew.inference.face_embeded.feedback_service";
            if (!string.IsNullOrEmpty(_config["RabbitMQ:FaceResultQueue"]))
            {
                queueName = _config["RabbitMQ:FaceResultQueue"]!;
                _logger.LogInformation($"[*] Using queue name from config: {queueName}");
            }
            else
            {
                _logger.LogInformation($"[*] Using default queue name: {queueName}");
            }

            await _channel.QueueDeclareAsync(
                queue: queueName, 
                durable: true, 
                exclusive: false, 
                autoDelete: false, 
                cancellationToken: stoppingToken
            );
            _logger.LogInformation($"[*] Connected to RabbitMQ and declared queue: {queueName}");

            string exchangeName = "cognibrew.inference";
            if (!string.IsNullOrEmpty(_config["RabbitMQ:FaceResultExchange"]))
            {
                exchangeName = _config["RabbitMQ:FaceResultExchange"]!;
                _logger.LogInformation($"[*] Using exchange from config: {exchangeName}");
            }
            else
            {
                _logger.LogInformation($"[*] Using default exchange: {exchangeName}");
            }
            string routingKey = "face.recognized";
            if (!string.IsNullOrEmpty(_config["RabbitMQ:FaceResultRoutingKey"]))
            {
                routingKey = _config["RabbitMQ:FaceResultRoutingKey"]!;
                _logger.LogInformation($"[*] Using routing key from config: {routingKey}");
            }
            else
            {
                _logger.LogInformation($"[*] Using default routing key: {routingKey}");
            }

            await _channel.QueueBindAsync(
                queue: queueName,
                exchange: exchangeName,
                routingKey: routingKey,
                cancellationToken: stoppingToken
            );
            _logger.LogInformation($"[*] Bound queue '{queueName}' to exchange '{exchangeName}' with routing key '{routingKey}'");

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();

                try
                {
                    FaceResult faceResult = await ProcessMessageAsync(body);
                    await _channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);

                    _logger.LogInformation($"Received and saved face result for: {faceResult.Username}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message from RabbitMQ");
                    // กรณี Error อาจจะ Nack เพื่อให้ข้อความกลับไปที่คิว (แล้วแต่การออกแบบ)
                    // await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                }
            };

            await _channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            _logger.LogInformation($"[*] Subscribed to RabbitMQ queue: {queueName}");

            // รอจนกว่า Service จะถูกสั่งหยุด
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ");
        }
    }

    public async Task<FaceResult> ProcessMessageAsync(byte[] body)
    {
        // 1. ถอดรหัส Protobuf
        FaceResult faceResult = FaceResult.Parser.ParseFrom(body);

        // 2. บันทึกลง MongoDB
        await SaveToMongoAsync(faceResult);

        return faceResult;
    }

    private async Task SaveToMongoAsync(FaceResult faceResult)
    {
        string deviceId = _config["DEVICE_ID"] ?? "unknown-device";
        string currentDate = DateTime.Now.ToString("yyyy-MM-dd"); // ใช้วันที่ปัจจุบัน

        // แปลง FaceResult เป็น FeedbackVector
        var newVector = new FeedbackVector
        {
            VectorId = faceResult.FaceId, //Guid.NewGuid().ToString(),
            Username = faceResult.Username,
            Embedding = faceResult.Embedding.ToList(),
            IsCorrect = null,  // รอให้ Frontend มาอัปเดตทีหลัง
            IsSynced = false   // ยังไม่ได้ถูกส่งไปส่วนกลาง
        };

        // ค้นหา Document ของเครื่องนี้ ในวันที่นี้
        var filter = Builders<DailyFeedbackDocument>.Filter.Eq(x => x.DeviceId, deviceId) &
                     Builders<DailyFeedbackDocument>.Filter.Eq(x => x.Date, currentDate);

        // คำสั่งอัปเดต: ถ้าเจอให้ Push ลง Array, ถ้าไม่เจอให้สร้าง Document ใหม่ (Upsert)
        var update = Builders<DailyFeedbackDocument>.Update
            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString()) // สร้าง ID ใหม่ถ้าเป็น Document ใหม่
            .Push(x => x.Vectors, newVector); // นำ vector ใหม่ต่อท้าย Array

        var options = new UpdateOptions { IsUpsert = true };

        var updateResult = await _collection.UpdateOneAsync(filter, update, options);
        try
        {
            _logger.LogInformation($"MongoDB update result: MatchedCount={updateResult.MatchedCount}, ModifiedCount={updateResult.ModifiedCount}, UpsertedId={updateResult.UpsertedId}");
            _logger.LogInformation($"Saved face result to MongoDB for device: {deviceId} on date: {currentDate}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging MongoDB update result");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Closing RabbitMQ connections...");
        try
        {
            // เช็คก่อนว่า Channel ยังเปิดอยู่ไหม ค่อยสั่งปิด
            if (_channel is not null && _channel.IsOpen)
            {
                await _channel.CloseAsync(cancellationToken);
            }

            // เช็คก่อนว่า Connection ยังเปิดอยู่ไหม ค่อยสั่งปิด
            if (_connection is not null && _connection.IsOpen)
            {
                await _connection.CloseAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // ถ้าเกิด Error ตอนกำลังจะปิด (เช่น มันชิงปิดไปก่อนเสี้ยววินาที) ก็แค่ Log เตือนเบาๆ พอ ไม่ต้องให้แอป/เทสต์พัง
            _logger.LogWarning($"RabbitMQ closure warning: {ex.Message}");
        }
        finally
        {
            // คืนค่าหน่วยความจำ
            _channel?.Dispose();
            _connection?.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}