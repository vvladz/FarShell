using FarShell.Client;

namespace FarShell.Tests;

public sealed class ConsoleModeScopeTests
{
    [Fact]
    public void RawVirtualTerminalInputPreservesMouseAndQuickEditModes()
    {
        const uint processedInput = 0x0001;
        const uint lineInput = 0x0002;
        const uint echoInput = 0x0004;
        const uint mouseInput = 0x0010;
        const uint quickEditMode = 0x0040;
        const uint extendedFlags = 0x0080;
        const uint virtualTerminalInput = 0x0200;
        const uint inputMode = processedInput
            | lineInput
            | echoInput
            | mouseInput
            | quickEditMode
            | extendedFlags;

        var result = ConsoleModeScope.GetRawVirtualTerminalInputMode(inputMode);

        Assert.Equal(
            mouseInput | quickEditMode | extendedFlags | virtualTerminalInput,
            result);
    }
}
