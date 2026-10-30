using System;
using System.Collections.Generic;

namespace Amiss.Contracts
{
    /// <summary>
    /// Small dotted version (major.minor.patch) used by framework API ranges
    /// and capability version floors. Parsing is strict: anything else is a
    /// load-gate rejection, never a guess.
    /// </summary>
    public readonly struct ContractVersion : IComparable<ContractVersion>
    {
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        public ContractVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public static bool TryParse(string text, out ContractVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text.Split('.');
            if (parts.Length != 3)
                return false;
            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 9)
                    return false;
                foreach (var c in parts[i])
                    if (c < '0' || c > '9')
                        return false;
                if (!int.TryParse(parts[i], out numbers[i]))
                    return false;
            }
            version = new ContractVersion(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        public int CompareTo(ContractVersion other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            return Patch.CompareTo(other.Patch);
        }

        public static bool operator <(ContractVersion a, ContractVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(ContractVersion a, ContractVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(ContractVersion a, ContractVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(ContractVersion a, ContractVersion b) => a.CompareTo(b) >= 0;

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }
}
