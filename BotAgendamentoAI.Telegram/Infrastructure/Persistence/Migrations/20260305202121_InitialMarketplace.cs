using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BotAgendamentoAI.Telegram.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarketplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tg_MessagesLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TelegramMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    RelatedJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagesLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tg_Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tg_Jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsUrgent = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddressText = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    PreferenceCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FinalAmount = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                    FinalNotes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Users_ClientUserId",
                        column: x => x.ClientUserId,
                        principalTable: "tg_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_Users_ProviderUserId",
                        column: x => x.ProviderUserId,
                        principalTable: "tg_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tg_ProvidersProfile",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CategoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RadiusKm = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgRating = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    TotalReviews = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaseLatitude = table.Column<double>(type: "REAL", nullable: true),
                    BaseLongitude = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvidersProfile", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_ProvidersProfile_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "tg_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tg_UserSessions",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    ChatJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    ChatPeerUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    IsChatActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "tg_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tg_JobPhotos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelegramFileId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobPhotos_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "tg_Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tg_Ratings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Stars = table.Column<int>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ratings_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "tg_Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tg_ProviderPortfolioPhotos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    FileIdOrUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProviderProfileUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderPortfolioPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderPortfolioPhotos_ProvidersProfile_ProviderProfileUserId",
                        column: x => x.ProviderProfileUserId,
                        principalTable: "tg_ProvidersProfile",
                        principalColumn: "UserId");
                    table.ForeignKey(
                        name: "FK_ProviderPortfolioPhotos_Users_ProviderUserId",
                        column: x => x.ProviderUserId,
                        principalTable: "tg_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobPhotos_JobId_CreatedAt",
                table: "tg_JobPhotos",
                columns: new[] { "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ClientUserId_CreatedAt",
                table: "tg_Jobs",
                columns: new[] { "ClientUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ProviderUserId_Status_UpdatedAt",
                table: "tg_Jobs",
                columns: new[] { "ProviderUserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_Status_CreatedAt",
                table: "tg_Jobs",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessagesLog_RelatedJobId_CreatedAt",
                table: "tg_MessagesLog",
                columns: new[] { "RelatedJobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessagesLog_TenantId_TelegramUserId_CreatedAt",
                table: "tg_MessagesLog",
                columns: new[] { "TenantId", "TelegramUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPortfolioPhotos_ProviderProfileUserId",
                table: "tg_ProviderPortfolioPhotos",
                column: "ProviderProfileUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPortfolioPhotos_ProviderUserId_CreatedAt",
                table: "tg_ProviderPortfolioPhotos",
                columns: new[] { "ProviderUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_JobId",
                table: "tg_Ratings",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_ProviderUserId_CreatedAt",
                table: "tg_Ratings",
                columns: new[] { "ProviderUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Role",
                table: "tg_Users",
                columns: new[] { "TenantId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_TelegramUserId",
                table: "tg_Users",
                columns: new[] { "TenantId", "TelegramUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ActiveJobId",
                table: "tg_UserSessions",
                column: "ActiveJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tg_JobPhotos");

            migrationBuilder.DropTable(
                name: "tg_MessagesLog");

            migrationBuilder.DropTable(
                name: "tg_ProviderPortfolioPhotos");

            migrationBuilder.DropTable(
                name: "tg_Ratings");

            migrationBuilder.DropTable(
                name: "tg_UserSessions");

            migrationBuilder.DropTable(
                name: "tg_ProvidersProfile");

            migrationBuilder.DropTable(
                name: "tg_Jobs");

            migrationBuilder.DropTable(
                name: "tg_Users");
        }
    }
}

