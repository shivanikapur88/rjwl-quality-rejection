using Microsoft.EntityFrameworkCore;
using Quality.Core.Entities;

namespace Quality.Data;

/// <summary>Master database (db/01_master_schema.sql). Schema is owned by the SQL scripts, not EF migrations.</summary>
public sealed class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options) { }

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<InspectionStage> InspectionStages => Set<InspectionStage>();
    public DbSet<ShopStage> ShopStages => Set<ShopStage>();
    public DbSet<UserShop> UserShops => Set<UserShop>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<RejectionPhenomenon> RejectionPhenomena => Set<RejectionPhenomenon>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("qa");
        b.Entity<Role>(e => { e.ToTable("Roles"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.RoleName).HasMaxLength(30); });
        b.Entity<User>(e =>
        {
            e.ToTable("Users"); e.HasKey(x => x.Id);
            e.Property(x => x.EmployeeCode).HasMaxLength(30); e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.DisplayName).HasMaxLength(100); e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
            e.HasMany(x => x.UserShops).WithOne().HasForeignKey(x => x.UserId);
        });
        b.Entity<Shop>(e => { e.ToTable("Shops"); e.Property(x => x.ShopCode).HasMaxLength(20); e.Property(x => x.ShopName).HasMaxLength(100); e.Property(x => x.EntryStoreKey).HasMaxLength(30); });
        b.Entity<InspectionStage>(e => { e.ToTable("InspectionStages"); e.Property(x => x.StageCode).HasMaxLength(30); e.Property(x => x.StageName).HasMaxLength(100); });
        b.Entity<ShopStage>(e =>
        {
            e.ToTable("ShopStages"); e.HasKey(x => new { x.ShopId, x.InspectionStageId });
            e.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId);
            e.HasOne(x => x.InspectionStage).WithMany().HasForeignKey(x => x.InspectionStageId);
        });
        b.Entity<UserShop>(e => { e.ToTable("UserShops"); e.HasKey(x => new { x.UserId, x.ShopId }); });
        b.Entity<Machine>(e => { e.ToTable("Machines"); e.Property(x => x.MachineCode).HasMaxLength(30); e.Property(x => x.MachineName).HasMaxLength(100); });
        b.Entity<RejectionPhenomenon>(e =>
        {
            e.ToTable("RejectionPhenomena");
            e.Property(x => x.PhenomenonCode).HasMaxLength(40);
            e.Property(x => x.PhenomenonName).HasMaxLength(200); e.Property(x => x.NormalizedName).HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)"); e.Property(x => x.ModifiedAt).HasColumnType("datetime2(0)");
            e.Property(x => x.RowVer).IsRowVersion();   // optimistic concurrency -> DbUpdateConcurrencyException -> HTTP 409
            e.HasOne(x => x.InspectionStage).WithMany().HasForeignKey(x => x.InspectionStageId);
        });
        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog"); e.Property(x => x.Action).HasMaxLength(50); e.Property(x => x.EntityType).HasMaxLength(50); e.Property(x => x.EntityId).HasMaxLength(50);
            e.Property(x => x.Timestamp).HasColumnType("datetime2(3)");
        });
    }
}

/// <summary>Entry database. The same context type is pointed at GDC_AndonDB or MachineShopDB (db/02a, 02b).</summary>
public sealed class EntryDbContext : DbContext
{
    public EntryDbContext(DbContextOptions<EntryDbContext> options) : base(options) { }
    public DbSet<QualityRejectionEntry> Entries => Set<QualityRejectionEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("qa");
        b.Entity<QualityRejectionEntry>(e =>
        {
            e.ToTable("QualityRejectionEntries"); e.HasKey(x => x.Id);
            e.Property(x => x.ShopCode).HasMaxLength(20); e.Property(x => x.StageCode).HasMaxLength(30); e.Property(x => x.StageName).HasMaxLength(100);
            e.Property(x => x.MachineCode).HasMaxLength(30); e.Property(x => x.MachineName).HasMaxLength(100);
            e.Property(x => x.PhenomenonNameSnapshot).HasMaxLength(200); e.Property(x => x.InspectorEmployeeCode).HasMaxLength(30);
            e.Property(x => x.Remarks).HasMaxLength(500);
            e.Property(x => x.Quantity).HasDefaultValue(1);
            e.Property(x => x.ServerCreatedAt).HasColumnType("datetime2(3)");
            e.HasIndex(x => x.SubmissionId).IsUnique();
        });
    }
}
