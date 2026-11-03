using System.Linq;
using NUnit.Framework;

namespace SpriteStudio.Editor.Tests
{
    public class SpriteStudioNamePopupTests
    {
        private static readonly string[] Names = { "a", "b" };

        private static string[] Shown(string[] names, string current, string emptyChoice, out string[] values)
        {
            values = SpriteStudioNamePopup.Choices(names, current, emptyChoice, out var options).ToArray();
            return options.Select(o => o.text).ToArray();
        }

        [Test]
        public void Choices_PutTheEmptyChoiceBeforeTheNames()
        {
            var shown = Shown(Names, "a", "(empty)", out var values);
            CollectionAssert.AreEqual(new[] { "", "a", "b" }, values);
            CollectionAssert.AreEqual(new[] { "(empty)", "a", "b" }, shown);
        }

        [Test]
        public void Choices_KeepANameTheAssetLacksAtTheTop()
        {
            var shown = Shown(Names, "gone", "(empty)", out var values);
            CollectionAssert.AreEqual(new[] { "gone", "", "a", "b" }, values);
            Assert.AreEqual("(missing) gone", shown[0]);
        }

        [Test]
        public void Choices_ShowNoneForAnEmptyValueThatIsNotAChoice()
        {
            var shown = Shown(Names, "", null, out var values);
            CollectionAssert.AreEqual(new[] { "", "a", "b" }, values);
            Assert.AreEqual("(none)", shown[0]);
        }

        [Test]
        public void Choices_AddNothingForMixedValues()
        {
            Shown(Names, null, null, out var values);
            CollectionAssert.AreEqual(Names, values);
        }

        [Test]
        public void Choices_SkipUnnamedAndRepeatedNames()
        {
            Shown(new[] { "a", "", "a", null, "b" }, "a", null, out var values);
            CollectionAssert.AreEqual(Names, values);
        }
    }
}
