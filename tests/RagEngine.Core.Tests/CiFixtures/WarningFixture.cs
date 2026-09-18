using System;
using Xunit;

namespace RagEngine.CiFixtures
{
    // This will generate a compiler warning because Obsolete attribute is used on OldMethod and it's invoked below.
    public class WarningFixture
    {
        [Obsolete("Synthetic warning for CI fixture - do not treat as error")]
        public static void OldMethod() { }

        [Fact]
        public void Trigger_Invokes_OldMethod()
        {
            // Calling obsolete method should generate a compiler warning, not an error
            OldMethod();
            Assert.True(true);
        }
    }
}
