using System.Runtime.CompilerServices;

// The state driver is internal; the tests drive it directly.
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.Tests")]
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.EditModeTests")]
[assembly: InternalsVisibleTo("SpriteStudio.UI.Editor")]
