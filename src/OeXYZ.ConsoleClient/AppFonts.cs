using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OeXYZ.ConsoleClient;

internal static class AppFonts
{
    private const string RegularResource = "OeXYZ.Fonts.Inter.Variable.ttf";
    private const string ItalicResource = "OeXYZ.Fonts.Inter.Italic.Variable.ttf";
    private static readonly PrivateFontCollection Collection = new();
    private static readonly List<GCHandle> FontBuffers = [];
    private static readonly FontFamily InterFamily;

    static AppFonts()
    {
        Load(RegularResource);
        Load(ItalicResource);
        InterFamily = Collection.Families.FirstOrDefault(family =>
            string.Equals(family.Name, "Inter", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The embedded Inter font could not be initialized.");
    }

    public static Font Create(float size, FontStyle style = FontStyle.Regular) =>
        new(InterFamily, size, SupportedStyle(style), GraphicsUnit.Point);

    private static FontStyle SupportedStyle(FontStyle requested)
    {
        if (InterFamily.IsStyleAvailable(requested)) return requested;
        if ((requested & FontStyle.Bold) != 0 && InterFamily.IsStyleAvailable(FontStyle.Bold))
            return FontStyle.Bold;
        if ((requested & FontStyle.Italic) != 0 && InterFamily.IsStyleAvailable(FontStyle.Italic))
            return FontStyle.Italic;
        return FontStyle.Regular;
    }

    private static void Load(string resourceName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' is missing.");
        if (stream.Length is <= 0 or > 16_777_216)
            throw new InvalidDataException($"Embedded font resource '{resourceName}' has an invalid size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Collection.AddMemoryFont(handle.AddrOfPinnedObject(), bytes.Length);
            FontBuffers.Add(handle);
        }
        catch
        {
            handle.Free();
            throw;
        }
    }
}
