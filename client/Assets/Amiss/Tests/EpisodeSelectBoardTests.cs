using Amiss.Contracts;
using Amiss.Shell.Episodes;
using Amiss.Shell.UI;
using NUnit.Framework;

namespace Amiss.Tests
{
    public class EpisodeSelectBoardTests
    {
        [Test]
        public void FirstEpisodeIsOpenAndTheOthersDisplayLocked()
        {
            var slots = EpisodeSelectBoard.Build(new[] { WhatIsTemperatureEpisode.Declaration() });

            Assert.AreEqual(EpisodeSelectBoard.MinimumSlots, slots.Length);
            Assert.IsFalse(slots[0].Locked);
            Assert.AreEqual(0, slots[0].SessionIndex);
            Assert.AreEqual("Temperature", slots[0].Title);
            Assert.AreEqual(
                "What determines the temperature of a group of balls?",
                slots[0].DetailTitle);
            Assert.AreEqual("15 MIN", slots[0].Duration);
            Assert.AreEqual("Open", slots[0].Status);
            StringAssert.Contains("Swing, throw, and predict", slots[0].Description);
            StringAssert.DoesNotContain("kinetic", slots[0].Description);

            for (var index = 1; index < slots.Length; index++)
            {
                Assert.IsTrue(slots[index].Locked, "slot " + slots[index].Number);
                Assert.AreEqual(-1, slots[index].SessionIndex);
                Assert.AreEqual(EpisodeSelectBoard.LockedTitle, slots[index].Title);
                Assert.AreEqual("Locked", slots[index].Status);
                Assert.AreEqual(index + 1, slots[index].Number);
            }
        }

        [Test]
        public void ALaterDeclarationStaysLocked()
        {
            var later = new EpisodeDeclaration
            {
                EpisodeId = "later_episode",
                CardQuestion = "What happens if the condition changes?",
                CardActionType = "Compare",
                Availability = EpisodeAvailability.Bundled,
                ExpectedDurationMinutes = 12
            };

            var slots = EpisodeSelectBoard.Build(new[]
            {
                WhatIsTemperatureEpisode.Declaration(),
                later
            });

            Assert.IsFalse(slots[0].Locked);
            Assert.IsTrue(slots[1].Locked);
            Assert.AreEqual("later episode", slots[1].Title);
            Assert.AreEqual(later.CardQuestion, slots[1].DetailTitle);
            Assert.AreEqual(-1, slots[1].SessionIndex);
            Assert.AreEqual(EpisodeSelectBoard.LockedTitle, slots[2].Title);
        }

        [Test]
        public void AnEmptyLibraryShowsOnlyLockedSlots()
        {
            var slots = EpisodeSelectBoard.Build(null);

            Assert.AreEqual(EpisodeSelectBoard.MinimumSlots, slots.Length);
            foreach (var slot in slots)
            {
                Assert.IsTrue(slot.Locked);
                Assert.AreEqual(-1, slot.SessionIndex);
            }
        }
    }
}
