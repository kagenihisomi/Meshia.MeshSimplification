# Meshia Mesh Simplification


- [English](#english)
- [日本語](#日本語)

[Documents](https://ramtype0.github.io/Meshia.MeshSimplification/)

## English
Mesh simplification tool/library for Unity, VRChat.

Based on Unity Job System, and Burst. 
Provides fast, asynchronous mesh simplification.

Can be executed at runtime or in the editor.

### Installation

### VPM

Add [my VPM repository](https://ramtype0.github.io/VpmRepository/) to VCC, then add Meshia Mesh Simplification package to your projects.


### How to use

### Development verification

Run package-wide compile verification locally:

```powershell
pwsh ./tools/verify-package.ps1
```

Install the repository pre-commit hook (one-time per clone):

```powershell
pwsh ./tools/install-hooks.ps1
```

After that, each commit runs package build verification automatically and blocks commits on compile errors.

#### NDMF integration

Attach `MeshiaMeshSimplifier` to your models.

You can preview the result in EditMode.


#### Use from C#

```csharp

using Meshia.MeshSimplification;

Mesh simplifiedMesh = new();

// Asynchronous API

await MeshSimplifier.SimplifyAsync(originalMesh, target, options, simplifiedMesh);

// Synchronous API

MeshSimplifier.Simplify(originalMesh, target, options, simplifiedMesh);

```

## 日本語

Unity、VRChat向けのメッシュ軽量化ツールです。
Unity Job Systemで動作するため、Burstと合わせて高速、かつ非同期で処理ができるのが特徴です。
ランタイム、エディターの双方で動作します。

### インストール

### VPM

[VPM repository](https://ramtype0.github.io/VpmRepository/)をVCCに追加してから、Manage Project > Manage PackagesからMeshia Mesh Simplificationをプロジェクトに追加してください。

### 使い方

### 開発時の検証

ローカルでパッケージ全体のコンパイル検証を実行:

```powershell
pwsh ./tools/verify-package.ps1
```

pre-commit hookの設定（クローンごとに1回）:

```powershell
pwsh ./tools/install-hooks.ps1
```

設定後はコミット時に自動でビルド検証が走り、コンパイルエラーがあるとコミットをブロックします。

#### NDMF統合

NDMFがプロジェクトにインポートされている場合、`MeshiaMeshSimplifier`が使えます。
エディターで軽量化結果をプレビューしながらパラメーターの調整ができます。

#### C#から呼び出す

```csharp

using Meshia.MeshSimplification;

Mesh simplifiedMesh = new();

// 非同期API

await MeshSimplifier.SimplifyAsync(originalMesh, target, options, simplifiedMesh);

// 同期API

MeshSimplifier.Simplify(originalMesh, target, options, simplifiedMesh);

```


