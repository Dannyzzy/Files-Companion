# vendor/RecycleBin

The built Modern Recycle Bin application, bundled so this installer can lay it
down without any network access. It is produced by the sibling project:

    https://github.com/Dannyzzy/Modern-Recycle-Bin

To refresh it, build that project and copy its output here:

    dist\RecycleBin.exe                  -> vendor\RecycleBin\
    lib\Microsoft.Web.WebView2.Core.dll  -> vendor\RecycleBin\
    lib\WebView2Loader.dll               -> vendor\RecycleBin\
    lib\WebView2-LICENSE.txt             -> vendor\RecycleBin\
    src\ui\index.html                    -> vendor\RecycleBin\ui\

Or run scripts\refresh-vendor.ps1, which does exactly that.

Both projects are MIT licensed; the WebView2 SDK terms are in
WebView2-LICENSE.txt.
