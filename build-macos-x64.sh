#!/bin/bash
set -e

APP_NAME="OppoPodsManager"
BUILD_DIR="bin/Release/net10.0/osx-x64/publish"
# The .app bundle must live OUTSIDE BUILD_DIR: the cp below would otherwise
# copy the freshly created shell into its own Contents/MacOS (nested .app).
APP_DIR="bin/Release/net10.0/osx-x64/${APP_NAME}.app"

echo "=== Building ${APP_NAME} for macOS x64 ==="

# Step 1: Publish self-contained
echo "[1/4] Publishing self-contained build..."
dotnet publish -c Release -r osx-x64 \
  --self-contained true \
  -o "${BUILD_DIR}"

# Step 2: Create .app bundle structure
echo "[2/4] Creating .app bundle..."
rm -rf "${APP_DIR}"
mkdir -p "${APP_DIR}/Contents/MacOS"
mkdir -p "${APP_DIR}/Contents/Resources"

# Copy ALL files from publish directory
# (drop stale .app shells left by older builds so they don't get nested)
rm -rf "${BUILD_DIR}/${APP_NAME}.app"
cp -R "${BUILD_DIR}"/* "${APP_DIR}/Contents/MacOS/"

# Step 3: Create Info.plist
echo "[3/4] Creating Info.plist..."
cat > "${APP_DIR}/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>OppoPodsManager</string>
    <key>CFBundleDisplayName</key>
    <string>OPPO Pods Manager</string>
    <key>CFBundleIdentifier</key>
    <string>com.oppo.podsmanager</string>
    <key>CFBundleVersion</key>
    <string>2.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>2.0.0-macos</string>
    <key>CFBundleExecutable</key>
    <string>OppoPodsManager</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSBluetoothAlwaysUsageDescription</key>
    <string>OPPO Pods Manager needs Bluetooth to connect to your earbuds.</string>
    <key>NSBluetoothPeripheralUsageDescription</key>
    <string>OPPO Pods Manager needs Bluetooth to connect to your earbuds.</string>
</dict>
</plist>
PLIST

# Step 4: Ad-hoc sign
echo "[4/4] Ad-hoc signing..."
codesign --force --deep --sign - "${APP_DIR}"

echo "Build complete: ${APP_DIR}"
