using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartLine.API.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    AlertType = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false
                    ),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TriggeredAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ResolvedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    UpdatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    UserName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedUserName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    Email = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedEmail = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "ConveyorSettings",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Speed = table.Column<int>(type: "integer", nullable: false),
                    IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                    IsEmergencyStopped = table.Column<bool>(type: "boolean", nullable: false),
                    IsFireDetected = table.Column<bool>(type: "boolean", nullable: false),
                    EmergencyResetGeneration = table.Column<int>(type: "integer", nullable: false),
                    FireDeviceEpoch = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    LastFireGeneration = table.Column<long>(type: "bigint", nullable: false),
                    IsFireGenerationFault = table.Column<bool>(type: "boolean", nullable: false),
                    Temperature = table.Column<decimal>(type: "numeric", nullable: false),
                    Humidity = table.Column<decimal>(type: "numeric", nullable: false),
                    FanActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConveyorSettings", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Barcode = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false
                    ),
                    Color = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: true
                    ),
                    Status = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    PassedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    UpdatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Statistics",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: true),
                    TotalProducts = table.Column<int>(type: "integer", nullable: false),
                    DefectiveProducts = table.Column<int>(type: "integer", nullable: false),
                    RedProducts = table.Column<int>(type: "integer", nullable: false),
                    BlueProducts = table.Column<int>(type: "integer", nullable: false),
                    GreenProducts = table.Column<int>(type: "integer", nullable: false),
                    YellowProducts = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statistics", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "SystemLogs",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    EventType = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false
                    ),
                    Message = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLogs", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserLogins",
                        x => new { x.LoginProvider, x.ProviderKey }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserTokens",
                        x => new
                        {
                            x.UserId,
                            x.LoginProvider,
                            x.Name,
                        }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ExpiresAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    RevokedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.InsertData(
                table: "ConveyorSettings",
                columns: new[]
                {
                    "Id",
                    "EmergencyResetGeneration",
                    "FanActive",
                    "FireDeviceEpoch",
                    "Humidity",
                    "IsEmergencyStopped",
                    "IsFireDetected",
                    "IsFireGenerationFault",
                    "IsRunning",
                    "LastFireGeneration",
                    "LastUpdated",
                    "Speed",
                    "Temperature",
                },
                values: new object[]
                {
                    1,
                    0,
                    false,
                    "",
                    45.0m,
                    false,
                    false,
                    false,
                    false,
                    0L,
                    new DateTime(2026, 8, 9, 0, 0, 0, 0, DateTimeKind.Utc),
                    50,
                    22.0m,
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_AlertType",
                table: "Alerts",
                column: "AlertType"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IsActive",
                table: "Alerts",
                column: "IsActive"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TriggeredAt",
                table: "Alerts",
                column: "TriggeredAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail"
            );

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Products_Color",
                table: "Products",
                column: "Color"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Products_PassedAt",
                table: "Products",
                column: "PassedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Products_Status",
                table: "Products",
                column: "Status"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId_ExpiresAt",
                table: "RefreshTokens",
                columns: new[] { "UserId", "ExpiresAt" }
            );

            migrationBuilder
                .CreateIndex(
                    name: "IX_Statistics_Date_Hour",
                    table: "Statistics",
                    columns: new[] { "Date", "Hour" },
                    unique: true
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_CreatedAt",
                table: "SystemLogs",
                column: "CreatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_EventType",
                table: "SystemLogs",
                column: "EventType"
            );

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_Severity",
                table: "SystemLogs",
                column: "Severity"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Alerts");

            migrationBuilder.DropTable(name: "AspNetRoleClaims");

            migrationBuilder.DropTable(name: "AspNetUserClaims");

            migrationBuilder.DropTable(name: "AspNetUserLogins");

            migrationBuilder.DropTable(name: "AspNetUserRoles");

            migrationBuilder.DropTable(name: "AspNetUserTokens");

            migrationBuilder.DropTable(name: "ConveyorSettings");

            migrationBuilder.DropTable(name: "Products");

            migrationBuilder.DropTable(name: "RefreshTokens");

            migrationBuilder.DropTable(name: "Statistics");

            migrationBuilder.DropTable(name: "SystemLogs");

            migrationBuilder.DropTable(name: "AspNetRoles");

            migrationBuilder.DropTable(name: "AspNetUsers");
        }
    }
}
