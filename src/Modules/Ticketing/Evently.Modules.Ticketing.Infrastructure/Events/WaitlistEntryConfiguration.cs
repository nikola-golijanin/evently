using Evently.Modules.Ticketing.Domain.Customers;
using Evently.Modules.Ticketing.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evently.Modules.Ticketing.Infrastructure.Events;

internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.HasKey(w => w.Id);

        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(w => w.EventId);

        builder.HasOne<TicketType>()
            .WithMany()
            .HasForeignKey(w => w.TicketTypeId);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(w => w.CustomerId);

        builder.Property(w => w.Status)
            .HasConversion<string>();

        builder.HasIndex(w => new { w.TicketTypeId, w.CustomerId })
            .IsUnique()
            .HasFilter("status IN ('Waiting', 'Offered')");
    }
}
