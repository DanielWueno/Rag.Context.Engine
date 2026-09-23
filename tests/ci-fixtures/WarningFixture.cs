using System;

namespace RagEngine.CiFixtures
{
    // This will generate a compiler warning because Obsolete attribute is used on OldMethod and it's invoked below.
    public class WarningFixture
    {
        [Obsolete("Synthetic warning for CI fixture - do not treat as error")]
        public static void OldMethod() { }

        public static void Trigger()
        {
            OldMethod();
        }
    }
}
