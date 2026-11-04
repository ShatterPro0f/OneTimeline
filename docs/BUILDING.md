# Building One Timeline

## Toolchain

- **.NET SDK 10** with the MAUI workload (`dotnet workload install maui-android`)
- **JDK 21**
- **Android SDK** with platform 36 and build-tools 36

The project targets `net10.0-android` and targets API 36.

## Build

```sh
dotnet build -c Release -f net10.0-android
```

The project is configured to produce an **Android App Bundle** (`app.onetimeline-Signed.aab`) for store-style distribution. For a directly installable APK from the same build, use bundletool:

```sh
java -jar bundletool.jar build-apks \
  --bundle bin/Release/net10.0-android/app.onetimeline-Signed.aab \
  --output app.apks --mode=universal \
  --ks <your-keystore> --ks-pass <…> --ks-key-alias <…> --key-pass <…>
```

or build with `-p:AndroidPackageFormat=apk`.

## Notes

- The app icon is `Resources/AppIcon/appicon.png` (a full-bleed square; Android generates the adaptive and legacy densities from it).
- There is **no built-in episode dataset**: since v2.1.0 every universe — One Chicago and CSI included — is a downloadable pack in `packs/` (the pack index is `packs/index.json`), installed at runtime and registered from its self-describing envelope (`Services/DatasetFile.cs`, `Services/PackService.cs`). The csproj refuses to embed dataset files (`MauiAsset Remove` on `Resources\Raw\episodes*.json`) so a pack can never sneak back into the APK.
- Episode descriptions refresh from TVmaze at runtime; the shipped watch order is **not** extended by refreshes — new episodes arrive as a new data version of the pack (the pack manager offers it as an update), so the order stays exactly the curated one.
- Signing: development builds use the standard Android debug key. Release builds should use one consistent key kept by the maintainer — Android will only install an update over an existing install if the signing key matches.
