# GrowTrack (SME CRM & Business Management Platform)

GrowTrack is an ASP.NET Core 8.0 MVC web application designed as an all-in-one Customer Relationship Management (CRM), Inventory & Point of Sale (POS), B2B Marketplace, and Business Intelligence platform for Small and Medium Enterprises (SMEs).

---

## Key Features

### 1. Multi-Role Authentication & Access Control
- **Admin Portal**: Approve or reject Business Owner registrations, verify business documents (Brgy. Clearance, Mayor's Permit, DTI/SEC registration), manage user accounts, and monitor platform-wide metrics.
- **Business Owner (BO)**: Full access to inventory, POS/Quick Sale, leads & marketing campaigns, staff management, calendar scheduling, marketplace publishing, and analytics reports.
- **Staff Accounts**: Granular permission-based access (`Inventory`, `Campaigns`, `Calendar`, `Leads`, `QuickSale`, `Reports`, `Notifications`, `Chat`) managed by the Business Owner with staff activity logging.
- **Security**: Cookie-based authentication (`GrowTrack.Auth`), BCrypt password hashing, device tracking, and optional Two-Factor Authentication (2FA) via email verification codes.

### 2. Inventory, Suppliers & Point of Sale (POS)
- **Inventory Management (`Inventory1Controller`)**: Create, categorize, and track products (`Products2`), stock levels, reorder alerts, SKU/barcodes, purchase/selling prices, and product images.
- **POS / Quick Sale**: Process customer sales (`Sales`, `SaleItems`), automatically deduct stock, log inventory movements (`InventoryLogs`), and generate transaction records.
- **Supplier Directory (`Supplier2Controller`)**: Manage supplier contacts and send direct email inquiries or restock requests.

### 3. CRM, Leads & Email Campaigns
- **Lead Management (`LeadsController`)**: Track prospective and existing customers, lead points/scoring, purchase history, and lead activity logs (`LeadActivityLogs`).
- **Marketing Campaigns (`CampaignsController` & `TrackingController`)**: Build and send targeted email campaigns using customizable message templates (`MessageTemplates`) with open/click tracking (`CampaignTracker`).

### 4. B2B Marketplace & Product Orders
- **Business Profiles (`BOBusinessProfileController`)**: Customizable storefront profiles with logo and background uploads.
- **Marketplace (`MarketplaceController` & `ProductOrdersController`)**: Publish products to the shared marketplace, browse offerings from other verified businesses, and manage buyer/seller product orders (`ProductOrders`).

### 5. Real-Time Collaboration & Scheduling
- **SignalR Real-Time Hub (`/realtimehub`)**: Powers live chat (`ChatController`), instant user/staff notifications (`NotificationsController`), and real-time online status indicators.
- **Interactive Calendar (`CalendarController`)**: FullCalendar-integrated event and task scheduling (`Calendar`, `CalendarTasks`) with priority levels and visibility controls.

### 6. Business Intelligence & Reporting
- **Analytics & Reports (`ReportsController`)**: Interactive dashboards and exportable reports (via `ClosedXML` for Excel exports) covering Sales History, Period-over-Period Sales Comparisons, Inventory Valuation, and Stock Movements.

---

## Tech Stack

- **Framework**: ASP.NET Core 8.0 MVC (`net8.0`)
- **Database & ORM**: Microsoft SQL Server with Entity Framework Core 9.0 (`Microsoft.EntityFrameworkCore.SqlServer`)
- **Real-Time Communication**: ASP.NET Core SignalR (`RealTimeHub`)
- **Authentication & Security**: ASP.NET Core Cookie Authentication, `BCrypt.Net-Next`, `BouncyCastle.Cryptography`
- **Document & Data Export**: `ClosedXML` (Excel generation), `Newtonsoft.Json`
- **Frontend**: Razor Views, Bootstrap 5.3, jQuery, DataTables, FullCalendar 5, Select2, SweetAlert2, Font Awesome 6

---

## Project Structure

```text
GrowTrack/
├── Project_Creation.sln                 # Visual Studio Solution file
├── README.md                            # Project documentation
└── Project_Creation/                    # Main ASP.NET Core MVC Web Project
    ├── Controllers/                     # MVC & API Controllers (Admin, Inventory1, Leads, Marketplace, etc.)
    ├── Data/
    │   └── AuthDbContext.cs             # EF Core DbContext & entity relationship configurations
    ├── DTO/                             # Data Transfer Objects & request/response payloads
    ├── Helpers/                         # Utility helpers (e.g., UpdateNotificationsHelper)
    ├── Middleware/
    │   └── ErrorHandlingMiddleware.cs   # Custom exception handling middleware
    ├── Migrations/                      # EF Core database migrations
    ├── Models/
    │   ├── Authorization/               # Custom authorization attributes (StaffAccessAttribute)
    │   ├── DTOs/                        # Calendar and domain DTOs
    │   ├── Entities/                    # Database entities (Users, Product, Sale, Leads, Campaign, etc.)
    │   └── ViewModels/                  # Razor ViewModels
    ├── Properties/
    │   └── launchSettings.json          # Local development launch profiles (HTTP 5099 / HTTPS 7063)
    ├── Services/                        # Application services (EmailService, CalendarNotificationService)
    ├── Views/                           # Razor views organized by controller
    ├── wwwroot/                         # Static assets (CSS, JS, images, uploads)
    ├── appsettings.json                 # Application configuration (DB connection, SMTP, uploads)
    └── Program.cs                       # Application entry point, DI, and middleware pipeline
```

---

## Getting Started (Running Locally)

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft SQL Server (LocalDB, SQL Server Express, or remote SQL Server instance)

### 1. Configure the Database & Settings
Review `Project_Creation/appsettings.json` (or configure User Secrets):
- **`ConnectionStrings:ProjectCreationDB`**: Points to the SQL Server database. To use a local SQL Server instance instead of the remote test server, update `ProjectCreationDB` to:
  ```json
  "Server=.;Database=ProjectCreationDB1;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True;"
  ```
- **`EMAIL_CONFIGURATION`**: SMTP settings used by `EmailService` for 2FA codes, password resets, and marketing campaigns.

### 2. Apply Database Migrations (if using a fresh local database)
```powershell
cd Project_Creation
dotnet ef database update
```

### 3. Build and Run the Application
From the root directory (`GrowTrack`):
```powershell
dotnet run --project Project_Creation --launch-profile http
```
Or with HTTPS:
```powershell
dotnet run --project Project_Creation --launch-profile https
```

### 4. Access the Application
- **HTTP URL**: `http://localhost:5099`
- **HTTPS URL**: `https://localhost:7063`
- **Initial Admin Setup**: If no admin exists in the database, navigate to `/Home/AdminCreateAccount` to create the initial administrator account.
