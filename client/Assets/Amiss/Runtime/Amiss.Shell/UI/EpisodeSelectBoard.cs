using System;
using System.Collections.Generic;
using Amiss.Contracts;
using UnityEngine;

namespace Amiss.Shell.UI
{
    /// <summary>
    /// The startup episode row. The first declaration is the only episode
    /// that can launch. Every later slot is shown locked. Empty slots pad
    /// the row out to the reference panel's four cards and are not
    /// declarations, so they never reach the load gate.
    /// </summary>
    public static class EpisodeSelectBoard
    {
        public const int MinimumSlots = 4;
        public const string LockedTitle = "Coming soon";
        public const string LockedDescription = "This episode is not in this build.";

        public static readonly Color Panel = new Color(0.031f, 0.039f, 0.051f, 0.94f);
        public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.35f);
        public static readonly Color Hairline = new Color(0.118f, 0.165f, 0.200f, 1f);
        public static readonly Color Accent = new Color(0.361f, 0.882f, 0.902f, 1f);
        public static readonly Color AccentDim = new Color(0.361f, 0.882f, 0.902f, 0.22f);
        public static readonly Color Text = new Color(0.94f, 0.96f, 0.98f, 1f);
        public static readonly Color Muted = new Color(0.541f, 0.588f, 0.639f, 1f);
        public static readonly Color CardFill = new Color(0.051f, 0.063f, 0.082f, 0.98f);
        public static readonly Color CardFillLocked = new Color(0.039f, 0.047f, 0.059f, 0.94f);
        public static readonly Color Icon = new Color(0.75f, 0.82f, 0.86f, 0.92f);
        public static readonly Color IconLocked = new Color(0.62f, 0.68f, 0.74f, 0.9f);

        public readonly struct Slot
        {
            public int Number { get; init; }
            public string Title { get; init; }
            public string DetailTitle { get; init; }
            public string Description { get; init; }
            public string Duration { get; init; }
            public string Status { get; init; }
            public bool Locked { get; init; }
            public int SessionIndex { get; init; }
        }

        public static Slot[] Build(IReadOnlyList<EpisodeDeclaration> declarations)
        {
            var count = declarations?.Count ?? 0;
            var slots = new Slot[Math.Max(MinimumSlots, count)];
            for (var index = 0; index < slots.Length; index++)
            {
                if (index < count && index == 0)
                    slots[index] = OpenSlot(declarations[index]);
                else if (index < count)
                    slots[index] = LockedDeclaration(index, declarations[index]);
                else
                    slots[index] = Placeholder(index);
            }

            return slots;
        }

        static Slot OpenSlot(EpisodeDeclaration declaration) => new()
        {
            Number = 1,
            Title = ShortTitle(declaration),
            DetailTitle = string.IsNullOrWhiteSpace(declaration.CardQuestion)
                ? ShortTitle(declaration)
                : declaration.CardQuestion,
            Description = Describe(declaration),
            Duration = declaration.ExpectedDurationMinutes > 0
                ? declaration.ExpectedDurationMinutes + " MIN"
                : "—",
            Status = declaration.Availability == EpisodeAvailability.Bundled ? "Open" : "Locked",
            Locked = declaration.Availability != EpisodeAvailability.Bundled,
            SessionIndex = 0
        };

        static Slot LockedDeclaration(int index, EpisodeDeclaration declaration)
        {
            var reason = string.IsNullOrWhiteSpace(declaration.AvailabilityReason)
                ? LockedDescription
                : declaration.AvailabilityReason;
            return new Slot
            {
                Number = index + 1,
                Title = ShortTitle(declaration),
                DetailTitle = string.IsNullOrWhiteSpace(declaration.CardQuestion)
                    ? ShortTitle(declaration)
                    : declaration.CardQuestion,
                Description = reason,
                Duration = "—",
                Status = "Locked",
                Locked = true,
                SessionIndex = -1
            };
        }

        static Slot Placeholder(int index) => new()
        {
            Number = index + 1,
            Title = LockedTitle,
            DetailTitle = LockedTitle,
            Description = LockedDescription,
            Duration = "—",
            Status = "Locked",
            Locked = true,
            SessionIndex = -1
        };

        public static string ShortTitle(EpisodeDeclaration declaration)
        {
            if (declaration == null || string.IsNullOrWhiteSpace(declaration.EpisodeId))
                return "Episode";
            if (declaration.EpisodeId == "what_is_temperature")
                return "Temperature";
            return declaration.EpisodeId.Replace('_', ' ');
        }

        public static string Describe(EpisodeDeclaration declaration)
        {
            var action = string.IsNullOrWhiteSpace(declaration.CardActionType)
                ? "Choose this episode to begin."
                : declaration.CardActionType.Trim().TrimEnd('.') + ".";
            var space = declaration.SceneForm == "full_room" ? " The trial uses the whole room." : "";
            return action + space;
        }

        static Sprite ring;
        static Sprite rounded;

        public static Sprite Ring => ring != null ? ring : ring = MakeRing();

        public static Sprite Rounded => rounded != null ? rounded : rounded = MakeRounded();

        static Sprite MakeRing()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var center = (size - 1) * 0.5f;
            const float outer = 30f;
            const float inner = 26f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    var alpha = distance <= outer && distance >= inner ? 1f : 0f;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        static Sprite MakeRounded()
        {
            const int size = 96;
            const int radius = 22;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var px = x + 0.5f;
                    var py = y + 0.5f;
                    var dx = px < radius ? radius - px : px > size - radius ? px - (size - radius) : 0f;
                    var dy = py < radius ? radius - py : py > size - radius ? py - (size - radius) : 0f;
                    var alpha = dx * dx + dy * dy <= radius * radius ? 1f : 0f;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();
            return Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }
    }
}
