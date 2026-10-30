using System;

namespace Amiss.Contracts
{
    /// <summary>
    /// On-device progress for one episode, supplied by the persistence owner.
    /// Progress supplies completion only; it never carries a conclusion or an
    /// inferred understanding.
    /// </summary>
    public sealed class EpisodeProgress
    {
        public bool CompletionStageReached { get; init; }
    }

    /// <summary>
    /// The shell's library card projection. Built from exactly two sources:
    /// the episode declaration and local progress. The visible fields are
    /// exactly the question, expected duration, required space, action type,
    /// completion state, and availability. The type deliberately has no
    /// conclusion field, so an episode conclusion cannot leak onto a card by
    /// renderer discipline alone.
    /// </summary>
    public sealed class LibraryCard
    {
        public string EpisodeId { get; init; } = "";
        public string Question { get; init; } = "";
        public int ExpectedDurationMinutes { get; init; }
        public string RequiredSpaceSummary { get; init; } = "";
        public string ActionType { get; init; } = "";
        public bool Completed { get; init; }
        public EpisodeAvailability Availability { get; init; } = EpisodeAvailability.Bundled;
        public string AvailabilityReason { get; init; } = "";

        public static LibraryCard Project(EpisodeDeclaration declaration, EpisodeProgress progress)
        {
            if (declaration == null) throw new ArgumentNullException(nameof(declaration));
            progress ??= new EpisodeProgress();

            return new LibraryCard
            {
                EpisodeId = declaration.EpisodeId,
                Question = declaration.CardQuestion,
                ExpectedDurationMinutes = declaration.ExpectedDurationMinutes,
                RequiredSpaceSummary = BuildSpaceSummary(declaration),
                ActionType = declaration.CardActionType,
                Completed = progress.CompletionStageReached,
                Availability = declaration.Availability,
                AvailabilityReason = declaration.AvailabilityReason
            };
        }

        static string BuildSpaceSummary(EpisodeDeclaration d)
        {
            if (d.MinimumVolumeM3 is not > 0.0)
                return "Required space is not declared.";
            var form = string.IsNullOrWhiteSpace(d.SceneForm) ? "unspecified form" : d.SceneForm;
            return $"Requires at least {d.MinimumVolumeM3:0.##} m3 ({form}).";
        }
    }
}
