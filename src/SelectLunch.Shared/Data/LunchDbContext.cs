using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Data;

public sealed class LunchDbContext(DbContextOptions<LunchDbContext> options)
    : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<LunchPoll> Polls => Set<LunchPoll>();
    public DbSet<PollCandidate> PollCandidates => Set<PollCandidate>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();
    public DbSet<MealRecord> MealRecords => Set<MealRecord>();
    public DbSet<ChannelDay> ChannelDays => Set<ChannelDay>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(50);
        });

        b.Entity<Restaurant>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.NormalizedName).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.Category)
                .WithMany(c => c.Restaurants)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LunchPoll>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
        });

        b.Entity<PollCandidate>(e =>
        {
            e.HasKey(x => new { x.PollId, x.RestaurantId });
            e.HasOne(x => x.Poll).WithMany(p => p.Candidates).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PollVote>(e =>
        {
            // 1인 1표. 다시 누르면 기존 행을 갱신한다.
            e.HasIndex(x => new { x.PollId, x.SlackUserId }).IsUnique();
            e.HasOne(x => x.Poll).WithMany(p => p.Votes).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MealRecord>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);   // 추천 알고리즘의 기간 집계용
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChannelDay>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.Date });
        });
    }
}
