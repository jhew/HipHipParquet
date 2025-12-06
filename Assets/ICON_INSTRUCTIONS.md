# Icon Setup Instructions

## Current Status
The `app.ico` file is actually a PNG image. While this works for the WPF window icon, it won't work as a proper Windows application icon for the taskbar or exe file.

## To Create a Proper ICO File

### Option 1: Online Converter
1. Use a service like https://convertico.com/ or https://www.icoconverter.com/
2. Upload `app.png`
3. Select multiple icon sizes: 16x16, 32x32, 48x48, 64x64, 128x128, 256x256
4. Download the generated `.ico` file
5. Replace `Assets/app.ico` with the proper ICO file

### Option 2: Using ImageMagick (Command Line)
```bash
magick convert app.png -define icon:auto-resize=256,128,64,48,32,16 app.ico
```

### Option 3: Using GIMP (Free Software)
1. Open `app.png` in GIMP
2. File → Export As
3. Choose `.ico` format
4. Select multiple sizes in the export dialog
5. Save to `Assets/app.ico`

## After Creating Proper ICO

Update `HipHipParquet.csproj` to include:
```xml
<PropertyGroup>
    ...
    <ApplicationIcon>Assets\app.ico</ApplicationIcon>
</PropertyGroup>
```

This will:
- Display the icon in the Windows taskbar
- Embed the icon in the compiled .exe file
- Show the icon in Windows Explorer for the executable

## MSIX Package Icons

For MSIX packaging, you also need PNG assets:
- Square44x44Logo.png (App list icon)
- Square150x150Logo.png (Start menu tile)
- Wide310x150Logo.png (Wide tile)
- StoreLogo.png (Store listing)

These should be created from your app.png at the appropriate sizes with proper padding/margins per Windows design guidelines.
