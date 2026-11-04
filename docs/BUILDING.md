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
- The built-in episode dataset is `Resources/Raw/episodes.json`, wrapped by the app in a universe envelope at the persistence boundary (`Services/DatasetFile.cs`). The dataset's shape is versioned per universe (`"universe": "one-chicago"`).
- Episode descriptions refresh from TVmaze at runtime; the shipped watch order is **not** extended by refreshes — new episodes arrive through app updates, so the order stays exactly the curated one.
- Signing: development builds use the standard Android debug key. Release builds should use one consistent key kept by the maintainer — Android will only install an update over an existing install if the signing key matches.
