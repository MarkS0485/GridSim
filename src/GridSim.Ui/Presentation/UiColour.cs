namespace GridSim.Ui.Presentation;

/// <summary>
/// A colour as four bytes (alpha, red, green, blue), with no user-interface framework behind it. This is the colour
/// token the platform-neutral layer hands out: a front end turns it into its own brush (the WPF app through
/// <c>Palette.Frozen</c>, the planned Avalonia app through its own converter). It carries exactly the bytes the old
/// WPF <c>System.Windows.Media.Color</c> carried, so a colour that crosses this boundary and back is the same colour,
/// byte for byte.
/// </summary>
/// <param name="A">Alpha, 0 (transparent) to 255 (opaque).</param>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
public readonly record struct UiColour(byte A, byte R, byte G, byte B);
