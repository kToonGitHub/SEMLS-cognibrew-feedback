using FeedbackService.Controllers;
using FeedbackService.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
// >>> 1. เพิ่ม Using เหล่านี้เข้ามาสำหรับการทำ Mock Auth <<<
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost; // สำหรับ ConfigureTestServices
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Testcontainers.MongoDb;

namespace FeedbackService.Tests.Integration;

// >>> 2. สร้าง Class สำหรับ Bypass การตรวจ Token <<<
public class FakePolicyEvaluator : IPolicyEvaluator
{
    public virtual async Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
    {
        // สร้างข้อมูล User จำลอง (เผื่อ Controller ของคุณมีการดึง User.Identity.Name ไปใช้)
        var principal = new ClaimsPrincipal();
        principal.AddIdentity(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, "TestUser")
        }, "FakeScheme"));

        return await Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,
            new AuthenticationProperties(), "FakeScheme")));
    }

    public virtual async Task<PolicyAuthorizationResult> AuthorizeAsync(AuthorizationPolicy policy,
        AuthenticateResult authenticationResult, HttpContext context, object resource)
    {
        // บังคับให้การเช็คสิทธิ์ทุกอย่างผ่านเสมอ
        return await Task.FromResult(PolicyAuthorizationResult.Success());
    }
}

// IAsyncLifetime ช่วยให้เรา Start Container ก่อนรันเทสต์ และ Stop หลังรันเสร็จได้
public class FeedbackIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [Obsolete]
    public FeedbackIntegrationTests()
    {
        _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:latest")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _mongoContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // >>> 3. เปลี่ยนมาใช้ ConfigureTestServices เพื่อรับประกันว่ามันจะ Override ทับของจริงใน Program.cs แน่นอน <<<
            builder.ConfigureTestServices(services =>
            {
                // แทรก Fake Auth เข้าไปในระบบ
                services.AddSingleton<IPolicyEvaluator, FakePolicyEvaluator>();

                // เตะ MongoDB เดิมทิ้ง
                var descriptors = services.Where(d =>
                    d.ServiceType == typeof(IMongoCollection<DailyFeedbackDocument>) ||
                    d.ServiceType == typeof(MongoClient)).ToList();
                foreach (var d in descriptors) services.Remove(d);

                // เชื่อมกับ Testcontainer
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

        var payload = new FeedbackUpdateRequest(false);

        // Act: ยิงผ่าน HTTP จริงๆ (รอบนี้จะยิงผ่าน [Authorize] ได้ฉลุยโดยไม่ต้องแนบ Token)
        var response = await _client.PutAsJsonAsync("/api/v1/feedback/edge-001/2026-03-23/vec-real-01", payload);

        // Assert 1: HTTP ต้องตอบกลับสำเร็จ
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert 2: ข้อมูลใน Database ต้องถูกเปลี่ยนจริงๆ
        var updatedDoc = await collection.Find(x => x.DeviceId == "edge-001").FirstOrDefaultAsync();
        var targetVector = updatedDoc.Vectors.First(v => v.VectorId == "vec-real-01");

        targetVector.IsCorrect.Should().BeFalse();
    }
}