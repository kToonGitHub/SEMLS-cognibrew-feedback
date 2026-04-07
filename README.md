# Cognibrew Feedback Service

Feedback Service เป็น Microservice ที่พัฒนาด้วย **.NET 9 (ASP.NET Core)** ทำหน้าที่เป็นตัวกลางในการจัดการผลลัพธ์การจดจำใบหน้า (Facial Recognition) ที่ถูกส่งมาจาก Edge Devices โดยระบบจะรับข้อมูลผ่าน RabbitMQ, บันทึกสถานะลง MongoDB, เปิด API ให้ผู้ใช้งานทำการยืนยันความถูกต้อง (Feedback) และจะคอยกวาดข้อมูลที่ยืนยันแล้วส่งกลับไปยังระบบส่วนกลาง (Central Gateway) แบบอัตโนมัติ

## 🌟 ฟีเจอร์หลัก (Key Features)

1. **RabbitMQ Consumer (`FaceResultConsumerService`)**: คอยรับข้อความที่ถูกเข้ารหัสด้วย **Protobuf** จาก Edge Device นำมาถอดรหัสและบันทึกลงฐานข้อมูล
2. **Optimized MongoDB Storage**: จัดเก็บข้อมูลแบบรายวัน (Daily Document) และใช้เทคนิค `Upsert` ร่วมกับ `$push` Array เพื่อลดความซ้ำซ้อนของข้อมูลและรองรับปริมาณข้อมูลที่สูง
3. **RESTful API (`FeedbackController`)**: เปิด Endpoints ให้ Frontend หรือผู้ใช้งานทำการกดอัปเดตสถานะ (ถูก/ผิด) ของผลลัพธ์การสแกนใบหน้า
4. **Background Batch Sync (`FeedbackBatchSenderService`)**: ระบบกวาดข้อมูลเบื้องหลังที่จะทำงานตามรอบเวลา (Timer) เพื่อดึงข้อมูลที่ผู้ใช้อัปเดตแล้ว แต่ยังไม่ได้ส่ง (`IsSynced == false`) นำไปส่งให้ระบบ Gateway และอัปเดตสถานะเมื่อส่งสำเร็จ
5. **Robust Testing**: ครอบคลุมการทดสอบทั้ง Unit Test, Sociable Test และ Integration Test โดยใช้ `xUnit`, `Moq` และ `Testcontainers` (สร้าง Database จำลองสำหรับการทดสอบ)

## 🛠️ Tech Stack

- **Framework:** .NET 9.0
- **Database:** MongoDB
- **Message Broker:** RabbitMQ
- **Serialization:** Protocol Buffers (Protobuf)
- **Containerization:** Docker & Docker Compose
- **Testing:** xUnit, FluentAssertions, Moq, Testcontainers

---

## 🚀 วิธีการติดตั้งและรันระบบ (Getting Started)

### สิ่งที่ต้องมี (Prerequisites)
- [Docker](https://www.docker.com/) และ Docker Compose
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (สำหรับการพัฒนาและรันเทสต์ในเครื่อง)

### การรันระบบด้วย Docker Compose (แนะนำ)
ระบบถูกตั้งค่าให้พร้อมทำงานผ่าน Container โดยต้องอยู่ใน Network เดียวกับ RabbitMQ และ MongoDB

1. Clone repository
~~~bash
git clone https://github.com/kToonGitHub/SEMLS-cognibrew-feedback.git
cd SEMLS-cognibrew-feedback
~~~

2. ตรวจสอบตั้งค่าใน `docker-compose.yaml` (สามารถแก้ตัวแปร Environment ให้ตรงกับระบบของคุณ)
~~~yaml
environment:
  - ASPNETCORE_ENVIRONMENT=Production
  - ConnectionStrings__MongoDb=mongodb://root:example@feedback-mongodb:27017/?authSource=admin
  - RabbitMQ__HostName=cognibrew-rabbitmq
  - RabbitMQ__UserName=guest
  - RabbitMQ__Password=guest
  - DEVICE_ID=edge-001
  - SYNC_INTERVAL_SECONDS=5
  - GATEWAY_API_URL=http://gateway-service:8000/api/v1/gateway/batch
~~~

3. สั่งรัน Container
~~~bash
docker compose up -d --build
~~~

ระบบจะรันขึ้นมาที่พอร์ต `5000` ของเครื่อง Host (หรือ `8080` ภายใน Container) และสามารถเข้าดู Swagger UI ได้ที่ `http://localhost:5000/swagger`

---

## 📡 API Endpoints

### 1. อัปเดต Feedback การจดจำใบหน้า
- **URL:** `/api/v1/feedback/{deviceId}/{date}/{vectorId}`
- **Method:** `PUT`
- **Request Body (JSON):**
~~~json
{
  "isCorrect": true
}
~~~
- **Responses:**
  - `200 OK`: อัปเดตสำเร็จ
  - `400 Bad Request`: รูปแบบวันที่ไม่ถูกต้อง (ต้องเป็น `yyyy-MM-dd`)
  - `404 Not Found`: ไม่พบข้อมูล Document ของวันและอุปกรณ์นั้นๆ

---

## 🧪 การทดสอบระบบ (Testing)

โปรเจกต์นี้มาพร้อมกับการทดสอบเต็มรูปแบบ (Unit Test & Integration Test) หากต้องการรันเทสต์ ให้แน่ใจว่า **Docker Desktop เปิดทำงานอยู่** (เนื่องจากมีการใช้ `Testcontainers` เพื่อสร้าง MongoDB ขึ้นมาทดสอบ)

เข้าไปที่โฟลเดอร์ของโปรเจกต์และรันคำสั่ง:

~~~bash
dotnet test
~~~

**สิ่งที่ Test ครอบคลุม:**
- การทำงานของ Feedback Controller (Mock Database)
- การประมวลผลข้อความ Protobuf ที่เข้ามาจาก RabbitMQ
- เงื่อนไขการจำกัดข้อมูลและหลีกเลี่ยงการสร้าง Document ซ้ำซ้อน (Concurrency test)
- การยิงข้อมูล Batch ไปยัง Gateway แบบจำลองสถานะ 200 OK และ 500 ล่ม

---

## 📂 โครงสร้างโฟลเดอร์ (Project Structure)

~~~text
SEMLS-cognibrew-feedback/
├── docker-compose.yaml         # สำหรับ Deploy ระบบบน Docker
├── FeedbackService.sln         # ไฟล์ Solution ของ .NET
├── FeedbackService/            # โฟลเดอร์โปรเจกต์หลัก
│   ├── Controllers/            # API Endpoints
│   ├── Models/                 # โครงสร้างข้อมูล (MongoDB Document & DTOs)
│   ├── Protos/                 # ไฟล์ตั้งค่า Protobuf (.proto)
│   ├── Services/               # Background Services (RabbitMQ, Batch Sender)
│   ├── Dockerfile              # คำสั่งสร้าง Image
│   └── Program.cs              # จุดเริ่มต้นของระบบ และ Dependency Injection
└── FeedbackService.Tests/      # โปรเจกต์สำหรับการทดสอบ (xUnit)
~~~