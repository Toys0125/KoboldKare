using KoboldKare.Basis.Networking;
using NUnit.Framework;

namespace KoboldKare.Basis.Tests
{
    public sealed class KoboldKareLegacyViewIdTests
    {
        [Test]
        public void RootViewId_UsesFirstThirtyOneBitsOfInstanceId()
        {
            Assert.IsTrue(KoboldKareLegacyViewId.TryFromInstanceId(
                "8000002a000000000000000000000000",
                out int viewId));
            Assert.AreEqual(42, viewId);
        }

        [Test]
        public void SubViewIds_AreDeterministicAndDistinct()
        {
            const string instanceId = "1234567890abcdef1234567890abcdef";
            Assert.IsTrue(KoboldKareLegacyViewId.TryFromInstanceId(instanceId, 1, out int a));
            Assert.IsTrue(KoboldKareLegacyViewId.TryFromInstanceId(instanceId, 1, out int b));
            Assert.IsTrue(KoboldKareLegacyViewId.TryFromInstanceId(instanceId, 2, out int c));
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.Greater(a, 0);
            Assert.Greater(c, 0);
        }
    }
}
