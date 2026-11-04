using System.Globalization;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The founder-set energy balance unit (2026-11-04). One quantum is a
    /// declared, uncalibrated candidate: a fist hit adds 1, a bat strike adds
    /// 10, the cloth bag absorbs 10, and one ball crossing the palm trap loses
    /// 1. A tool slower than <see cref="MinimumStrikeSpeedMPerS"/> adds no
    /// energy at all, so a held-still hand cannot warm the balls. The writer
    /// still measures every applied change on its own state, so the ledger
    /// keeps signing real work; only the tool strength is fixed instead of
    /// depending on impact speed.
    /// </summary>
    public static class EnergyQuantum
    {
        public const string EvidenceStatus = "uncalibrated_declared_not_measured";

        /// <summary>Joules per balance unit. Ten units match the sandbag's
        /// previous nominal absorption on a scale-point ball.</summary>
        public const double JoulesPerUnit = 0.05;

        public const int FistUnits = 1;
        public const int BatUnits = 10;
        public const int SandbagUnits = 10;
        public const int PalmPassUnits = 1;

        /// <summary>
        /// A tool slower than this adds no energy at all. A held-still bat or
        /// fist must not keep warming the balls through tracking jitter.
        /// Matches the declared candidate swing-exit speed.
        /// </summary>
        public const double MinimumStrikeSpeedMPerS = 1.0;

        public const double FistJ = JoulesPerUnit * FistUnits;
        public const double BatJ = JoulesPerUnit * BatUnits;
        public const double SandbagJ = JoulesPerUnit * SandbagUnits;
        public const double PalmPassJ = JoulesPerUnit * PalmPassUnits;

        public static string JoulesText(double joules) =>
            joules.ToString("0.###", CultureInfo.InvariantCulture) + " J";
    }
}
