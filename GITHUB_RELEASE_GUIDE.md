# GitHub 发布指南

## 📋 准备工作

### 1. 确认文件已准备好
- ✅ `.gitignore` - 已配置
- ✅ `README.md` - 已更新
- ✅ `CHANGELOG.md` - 已创建
- ✅ `releases/StrmTool-v1.0.0.zip` - 已打包
- ✅ `releases/RELEASE_NOTES_v1.0.0.md` - 发布说明
- ✅ 版本号已更新为 1.0.0

### 2. 检查编译结果
```bash
# 确认 DLL 文件存在
ls bin/Release/net9.0/StrmTool.dll

# 确认 ZIP 文件存在
ls releases/StrmTool-v1.0.0.zip
```

## 🚀 上传到 GitHub

### 步骤 1：初始化 Git 仓库（如果还没有）

```bash
cd d:\project\StrmTool
git init
git add .
git commit -m "Initial commit - StrmTool v1.0.0"
```

### 步骤 2：创建 GitHub 仓库

1. 访问 https://github.com/new
2. 仓库名称：`StrmTool`
3. 描述：`Jellyfin plugin to extract media info from STRM files`
4. 选择 **Public**（公开）或 **Private**（私有）
5. **不要**勾选 "Initialize this repository with a README"
6. 点击 **Create repository**

### 步骤 3：推送代码到 GitHub

```bash
# 添加远程仓库（替换 yourusername 为你的 GitHub 用户名）
git remote add origin https://github.com/yourusername/StrmTool.git

# 推送代码
git branch -M main
git push -u origin main
```

### 步骤 4：创建 Release

#### 方法 1：通过 GitHub 网页

1. 访问你的仓库页面：`https://github.com/yourusername/StrmTool`
2. 点击右侧的 **Releases**
3. 点击 **Create a new release**
4. 填写信息：
   - **Tag version**: `v1.0.0`
   - **Release title**: `StrmTool v1.0.0`
   - **Description**: 复制 `releases/RELEASE_NOTES_v1.0.0.md` 的内容
5. 上传文件：
   - 点击 **Attach binaries by dropping them here or selecting them**
   - 上传 `releases/StrmTool-v1.0.0.zip`
6. 点击 **Publish release**

#### 方法 2：通过 GitHub CLI（推荐）

```bash
# 安装 GitHub CLI（如果还没有）
# Windows: winget install GitHub.cli
# macOS: brew install gh
# Linux: 参考 https://cli.github.com/manual/installation

# 登录 GitHub
gh auth login

# 创建 Release
gh release create v1.0.0 \
  releases/StrmTool-v1.0.0.zip \
  --title "StrmTool v1.0.0" \
  --notes-file releases/RELEASE_NOTES_v1.0.0.md
```

## 📝 更新 README 中的链接

发布后，更新 `README.md` 中的链接：

1. 将所有 `yourusername` 替换为你的 GitHub 用户名
2. 确认以下链接可用：
   - Release 下载链接
   - Issues 链接
   - License 链接

## 🏷️ 创建 Git Tag

```bash
# 创建标签
git tag -a v1.0.0 -m "Release version 1.0.0"

# 推送标签
git push origin v1.0.0
```

## 📊 发布后检查清单

- [ ] GitHub 仓库已创建
- [ ] 代码已推送到 main 分支
- [ ] Release v1.0.0 已创建
- [ ] ZIP 文件已上传到 Release
- [ ] Release 说明已添加
- [ ] README 中的链接已更新
- [ ] Git tag v1.0.0 已创建

## 🎯 后续步骤

### 1. 添加 GitHub Actions（可选）

创建 `.github/workflows/build.yml` 自动编译：

```yaml
name: Build

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 9.0.x
    - name: Restore dependencies
      run: dotnet restore
    - name: Build
      run: dotnet build -c Release --no-restore
    - name: Upload artifact
      uses: actions/upload-artifact@v3
      with:
        name: StrmTool
        path: bin/Release/net9.0/StrmTool.dll
```

### 2. 添加 Issue 模板（可选）

创建 `.github/ISSUE_TEMPLATE/bug_report.md` 和 `feature_request.md`

### 3. 添加贡献指南（可选）

创建 `CONTRIBUTING.md` 文件

### 4. 添加 GitHub Topics

在仓库页面点击 **Settings** → **Topics**，添加：
- `jellyfin`
- `jellyfin-plugin`
- `strm`
- `media-server`
- `csharp`
- `dotnet`

## 📢 宣传你的项目

1. **Jellyfin 论坛**：在 Jellyfin 社区分享你的插件
2. **Reddit**：在 r/jellyfin 发帖
3. **社交媒体**：分享到 Twitter、微博等

## 🔄 未来版本发布流程

当你准备发布新版本时：

```bash
# 1. 更新版本号
# 编辑 StrmTool.csproj，修改 AssemblyVersion 和 FileVersion

# 2. 更新 CHANGELOG.md
# 添加新版本的更新内容

# 3. 编译
dotnet build -c Release

# 4. 打包
Compress-Archive -Path "bin\Release\net9.0\StrmTool.dll" -DestinationPath "releases\StrmTool-v1.1.0.zip" -Force

# 5. 提交代码
git add .
git commit -m "Release v1.1.0"
git push

# 6. 创建 Release
gh release create v1.1.0 \
  releases/StrmTool-v1.1.0.zip \
  --title "StrmTool v1.1.0" \
  --notes "更新内容..."
```

---

**祝你的项目成功！** 🎉

