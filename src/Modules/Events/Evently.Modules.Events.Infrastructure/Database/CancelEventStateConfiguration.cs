using Evently.Modules.Events.Presentation.Events.CancelEventSaga;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evently.Modules.Events.Infrastructure.Database;

internal sealed class CancelEventStateConfiguration : IEntityTypeConfiguration<CancelEventState>
{
    public void Configure(EntityTypeBuilder<CancelEventState> builder)
    {
        builder.ToTable("cancel_event_saga_state", Schemas.Events);

        builder.HasKey(x => x.CorrelationId);

        builder.Property(x => x.CurrentState)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Version);
        builder.Property(x => x.CancellationCompletedStatus);
    }
}
