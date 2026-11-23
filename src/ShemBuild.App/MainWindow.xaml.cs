using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.WinUI.SharpDX;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ShemBuild.Core;
using ShemBuild.Rendering;
using ShemBuild.Resources;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace ShemBuild.App;

public sealed partial class MainWindow : Window
{
    private readonly SchematicReader _reader = new();
    private readonly MinecraftResourceDownloader _resourceDownloader = new();
    private readonly DefaultEffectsManager _effectsManager = new();
    private MinecraftResourcePack? _resourcePack;
    private Schematic? _schematic;

    public MainWindow()
    {
        InitializeComponent();
        Title = UiText.Get("ShemBuild — переглядач схем", "ShemBuild — Schematic Viewer");
        ResourcePackButton.Content = UiText.Get("Вибрати архів ресурсів…", "Choose resource archive…");
        OpenButton.Content = UiText.Get("Відкрити .schem", "Open .schem");
        FitButton.Content = UiText.Get("Показати всю споруду", "Fit structure");
        ResourcePackText.Text = UiText.Get(
            "Ресурси завантажаться автоматично за потреби · кеш у LocalAppData",
            "Resources download when needed · cache in LocalAppData");
        StructureHeaderText.Text = UiText.Get("Споруда", "Structure");
        StructureInfoText.Text = UiText.Get("Відкрийте файл .schem, щоб побачити його дані.", "Open a .schem file to see its details.");
        MaterialsHeaderText.Text = UiText.Get("Матеріали", "Materials");
        MaterialCountText.Text = UiText.Get("Типів блоків: 0", "Block types: 0");
        ShortcutHelpText.Text = UiText.Get("Ctrl+O — відкрити схему · F — показати всю споруду", "Ctrl+O — open schematic · F — fit structure");
        StatusText.Text = UiText.Get(
            "Відкрийте Sponge .schem. Якщо архів ресурсів не вибрано, застосунок завантажить офіційний клієнтський JAR Mojang і збереже його в локальний кеш.",
            "Open a Sponge .schem file. If no resource archive is selected, the app downloads the official Mojang client JAR and caches it locally.");

        var openAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.O,
            Modifiers = VirtualKeyModifiers.Control,
            ScopeOwner = RootLayout
        };
        openAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (OpenButton.IsEnabled)
            {
                OpenSchematic_Click(this, new RoutedEventArgs());
            }
        };
        RootLayout.KeyboardAccelerators.Add(openAccelerator);

        var fitAccelerator = new KeyboardAccelerator { Key = VirtualKey.F, ScopeOwner = RootLayout };
        fitAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (FitButton.IsEnabled)
            {
                FitScene_Click(this, new RoutedEventArgs());
            }
        };
        RootLayout.KeyboardAccelerators.Add(fitAccelerator);

        SceneViewport.EffectsManager = _effectsManager;
        SceneViewport.Items.Add(new AmbientLight3D { Color = Windows.UI.Color.FromArgb(255, 204, 204, 204) });
        SceneViewport.Items.Add(new DirectionalLight3D
        {
            Color = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            Direction = Vector3.Normalize(new Vector3(-1f, -1.5f, -1f))
        });
        SceneViewport.Camera = CreateCamera(16, 16, 16);
        Closed += (_, _) =>
        {
            _resourcePack?.Dispose();
            _effectsManager.Dispose();
        };
    }

    private async void SelectResourcePack_Click(object sender, RoutedEventArgs e)
    {
        ResourcePackButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".jar");
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            StatusText.Text = string.Format(UiText.Get("Читаю ресурси з {0}…", "Reading resources from {0}…"), file.Name);
            var selectedPack = await Task.Run(() => MinecraftResourcePack.Open(file.Path));
            _resourcePack?.Dispose();
            _resourcePack = selectedPack;
            ResourcePackText.Text = string.Format(UiText.Get("Ресурси: {0}", "Resources: {0}"), selectedPack.Name);
            StatusText.Text = _schematic is null
                ? UiText.Get("Ресурсний пакет вибрано. Тепер відкрийте .schem.", "Resource archive selected. Now open a .schem file.")
                : UiText.Get("Ресурсний пакет вибрано. Відкрийте схему ще раз, щоб перемалювати її з цими ресурсами.", "Resource archive selected. Reopen the schematic to render it with these resources.");
        }
        catch (ResourcePackException exception)
        {
            StatusText.Text = UiText.Get($"Не вдалося використати ресурсний пакет: {exception.Message}", "Could not read the selected resource archive. Choose a valid client JAR or resource-pack ZIP.");
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = UiText.Get("Не вдалося прочитати ресурсний пакет через обмеження доступу. Перевірте дозволи файлу.", "Could not read the resource archive because access was denied. Check the file permissions.");
        }
        catch (Exception)
        {
            StatusText.Text = UiText.Get("Не вдалося вибрати або прочитати .jar/.zip. Перевірте файл і спробуйте ще раз.", "Could not select or read the .jar/.zip file. Check the file and try again.");
        }
        finally
        {
            ResourcePackButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
        }
    }

    private async void OpenSchematic_Click(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = false;
        ResourcePackButton.IsEnabled = false;
        MinecraftResourcePack? pendingAutoPack = null;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".schem");
            picker.FileTypeFilter.Add(".schematic");
            picker.FileTypeFilter.Add(".nbt");
            picker.FileTypeFilter.Add(".mcstructure");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            FitButton.IsEnabled = false;
            StatusText.Text = string.Format(UiText.Get("Читаю {0}…", "Reading {0}…"), file.Name);
            var readResult = await Task.Run(() =>
            {
                var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
                var stopwatch = Stopwatch.StartNew();
                var schematic = _reader.Read(file.Path);
                stopwatch.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocationStart;
                return (Schematic: schematic, Elapsed: stopwatch.Elapsed, AllocatedBytes: allocatedBytes);
            });

            var versionSelection = await ShowVersionSelectionAsync(readResult.Schematic);
            if (versionSelection is null)
            {
                StatusText.Text = UiText.Get("Відкриття скасовано. Поточний перегляд залишився без змін.", "Opening cancelled. The current view is unchanged.");
                return;
            }

            var selectedPack = _resourcePack;
            AutoLoadedResourcePack? autoLoad = null;
            if (selectedPack is null)
            {
                var progress = new Progress<string>(message =>
                {
                    DispatcherQueue.TryEnqueue(() => StatusText.Text = message);
                });
                autoLoad = await _resourceDownloader.OpenForVersionAsync(
                    versionSelection.VersionId,
                    versionSelection.SelectionNote,
                    progress);
                selectedPack = autoLoad.ResourcePack;
                pendingAutoPack = selectedPack;
            }

            StatusText.Text = string.Format(UiText.Get("Будую текстуровану сцену з {0}…", "Building textured scene from {0}…"), selectedPack?.Name);
            var sceneResult = await Task.Run(() =>
            {
                var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
                var stopwatch = Stopwatch.StartNew();
                var scene = new MinecraftSceneBuilder().Build(readResult.Schematic, selectedPack);
                stopwatch.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocationStart;
                return (Scene: scene, Elapsed: stopwatch.Elapsed, AllocatedBytes: allocatedBytes);
            });

            var exposedFaces = sceneResult.Scene.ExposedFaces;
            var needsPerformanceWarning = exposedFaces > MinecraftSceneBuilder.FaceWarningThreshold;
            var performanceWarning = needsPerformanceWarning
                ? string.Format(UiText.Get(
                    "Попередження: понад {0:N0} відкритих граней; рендер може бути повільним або нестабільним. ",
                    "Warning: more than {0:N0} exposed faces; rendering may be slow or unstable. "), MinecraftSceneBuilder.FaceWarningThreshold)
                : string.Empty;
            if (needsPerformanceWarning)
            {
                StatusText.Text = performanceWarning + UiText.Get("Готую геометрію сцени…", "Preparing scene geometry…");
                await Task.Yield();
            }

            if (pendingAutoPack is not null)
            {
                _resourcePack?.Dispose();
                _resourcePack = pendingAutoPack;
                pendingAutoPack = null;
                ResourcePackText.Text = string.Format(UiText.Get("Автоматичні ресурси: Minecraft {0}", "Automatic resources: Minecraft {0}"), autoLoad!.VersionId);
            }

            SceneViewport.Items.Clear();
            SceneViewport.Items.Add(new AmbientLight3D { Color = Windows.UI.Color.FromArgb(255, 204, 204, 204) });
            SceneViewport.Items.Add(new DirectionalLight3D
            {
                Color = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                Direction = Vector3.Normalize(new Vector3(-1f, -1.5f, -1f))
            });

            var uiPreparation = Stopwatch.StartNew();
            foreach (var mesh in sceneResult.Scene.Meshes)
            {
                var geometryBuilder = new MeshBuilder(generateNormals: true, generateTexCoords: true);
                for (var index = 0; index + 2 < mesh.Vertices.Count; index += 3)
                {
                    var first = mesh.Vertices[index];
                    var second = mesh.Vertices[index + 1];
                    var third = mesh.Vertices[index + 2];
                    geometryBuilder.AddTriangle(
                        first.Position, second.Position, third.Position,
                        first.TextureCoordinate, second.TextureCoordinate, third.TextureCoordinate);
                }

                var material = new PhongMaterial
                {
                    DiffuseColor = new Color4(mesh.Tint.X, mesh.Tint.Y, mesh.Tint.Z, mesh.Tint.W),
                    AmbientColor = new Color4(0.12f, 0.12f, 0.12f, 1f),
                    SpecularColor = new Color4(0f, 0f, 0f, 1f),
                    SpecularShininess = 1,
                    DiffuseMap = mesh.TextureBytes is { Length: > 0 } textureBytes
                        ? new TextureModel(new MemoryStream(textureBytes, writable: false), autoCloseStream: true)
                        : null,
                    DiffuseMapSampler = HelixToolkit.SharpDX.Shaders.DefaultSamplers.PointSamplerWrap
                };

                SceneViewport.Items.Add(new MeshGeometryModel3D
                {
                    Geometry = geometryBuilder.ToMeshGeometry3D(),
                    Material = material,
                    IsTransparent = mesh.HasAlpha
                });
            }
            uiPreparation.Stop();

            var openedSchematic = readResult.Schematic;
            _schematic = openedSchematic;
            SceneViewport.Camera = CreateCamera(openedSchematic.Width, openedSchematic.Height, openedSchematic.Length);
            FitCameraToSchematic(openedSchematic);
            FitButton.IsEnabled = true;

            var name = string.IsNullOrWhiteSpace(openedSchematic.Name) ? file.Name : openedSchematic.Name;
            var dataVersion = openedSchematic.DataVersion is int version
                ? $"DataVersion: {version}"
                : UiText.Get("DataVersion: не вказано", "DataVersion: not provided");
            StructureInfoText.Text =
                $"{name}\n" +
                string.Format(UiText.Get("Розмір: {0} × {1} × {2}\n", "Size: {0} × {1} × {2}\n"), openedSchematic.Width, openedSchematic.Height, openedSchematic.Length) +
                string.Format(UiText.Get("Локальний початок: (0, 0, 0)\nЗсув вставлення: ({0}, {1}, {2})\n", "Local origin: (0, 0, 0)\nPaste offset: ({0}, {1}, {2})\n"), openedSchematic.OffsetX, openedSchematic.OffsetY, openedSchematic.OffsetZ) +
                $"Sponge v{openedSchematic.FormatVersion} · {dataVersion}\n" +
                string.Format(UiText.Get("Версія для ресурсів: Minecraft {0}", "Resource version: Minecraft {0}"), versionSelection.VersionId);
            MaterialCountText.Text = string.Format(
                UiText.Get("Типів блоків: {0} · непорожніх блоків: {1:N0}", "Block types: {0} · non-air blocks: {1:N0}"),
                openedSchematic.BlockCounts.Count,
                openedSchematic.NonAirBlockCount);
            MaterialList.ItemsSource = openedSchematic.BlockCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}  —  {pair.Value.ToString("N0", CultureInfo.CurrentCulture)}")
                .ToList();

            var memoryMb = (readResult.AllocatedBytes + sceneResult.AllocatedBytes) / (1024d * 1024d);
            var missingCount = sceneResult.Scene.MissingBlocks.Values.Sum();
            var missingSummary = missingCount == 0
                ? string.Empty
                : string.Format(UiText.Get(" · немає моделі/текстури для {0:N0} блоків ({1})", " · model/texture missing for {0:N0} blocks ({1})"), missingCount, string.Join(", ", sceneResult.Scene.MissingBlocks.Keys.Take(3)));
            var resourceSummary = autoLoad is null
                ? string.Format(UiText.Get(
                    "Ресурси: {0}. Версію Minecraft {1} вибрано окремо; відповідність цього архіву не перевірена. ",
                    "Resources: {0}. Minecraft {1} was selected separately; this archive was not version-checked."), selectedPack?.Name, versionSelection.VersionId)
                : string.Format(UiText.Get(
                    "{0} Ресурси Minecraft {1} {2}. ",
                    "{0} Minecraft {1} resources {2}. "),
                    autoLoad.SelectionNote,
                    autoLoad.VersionId,
                    autoLoad.WasCached ? UiText.Get("взято з кешу", "loaded from cache") : UiText.Get("завантажено й перевірено", "downloaded and verified"));

            StatusText.Text = string.Format(
                UiText.Get(
                    "{0} · {1} × {2} × {3} · {4:N0} блоків · формат v{5} · читання {6:N0} мс + побудова {7:N0} мс · {8:N0} граней · UI-геометрія {9:N0} мс · виділено приблизно {10:N1} МБ. ",
                    "{0} · {1} × {2} × {3} · {4:N0} blocks · format v{5} · read {6:N0} ms + build {7:N0} ms · {8:N0} faces · UI geometry {9:N0} ms · about {10:N1} MB allocated. "),
                name, openedSchematic.Width, openedSchematic.Height, openedSchematic.Length,
                openedSchematic.NonAirBlockCount, openedSchematic.FormatVersion,
                readResult.Elapsed.TotalMilliseconds, sceneResult.Elapsed.TotalMilliseconds,
                exposedFaces, uiPreparation.Elapsed.TotalMilliseconds, memoryMb) +
                performanceWarning + resourceSummary + missingSummary +
                UiText.Get("Вихідний файл не змінювався.", "The source file was not modified.");
        }
        catch (SchematicReadException exception)
        {
            StatusText.Text = UiText.IsEnglish
                ? "Could not open schematic. Only GZip Sponge Java Edition .schem v2/v3 is supported; the file may be damaged, unsupported, or exceed a prototype limit. Choose another file."
                : $"Не вдалося відкрити схему: {exception.Message}";
        }
        catch (ResourcePackException exception)
        {
            StatusText.Text = UiText.Get($"Помилка ресурсного пакета: {exception.Message}", "The selected resource archive is invalid or does not contain the required Minecraft assets. Choose another archive.");
        }
        catch (ResourceDownloadException exception)
        {
            StatusText.Text = UiText.IsEnglish
                ? "Could not load resources for the selected Minecraft version. Choose an archive manually or retry with an internet connection."
                : $"Не вдалося завантажити ресурси вибраної версії Minecraft: {exception.Message} Виберіть архів вручну або спробуйте знову з підключенням до інтернету.";
        }
        catch (JsonException)
        {
            StatusText.Text = UiText.Get("Ресурсний пакет містить некоректний JSON у файлі моделі або стану блока. Виберіть інший пакет.", "The resource archive contains invalid JSON in a model or block-state file. Choose another archive.");
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = UiText.Get("Не вдалося прочитати файл або записати локальний кеш ресурсів. Перевірте дозволи та спробуйте ще раз.", "Could not read the file or write the local resource cache. Check permissions and try again.");
        }
        catch (Exception exception)
        {
            StatusText.Text = UiText.IsEnglish
                ? "Could not display the schematic because an unexpected rendering error occurred. Choose another file or resource archive."
                : $"Не вдалося показати схему: {exception.Message}";
        }
        finally
        {
            pendingAutoPack?.Dispose();
            OpenButton.IsEnabled = true;
            ResourcePackButton.IsEnabled = true;
            FitButton.IsEnabled = _schematic is not null;
        }
    }

    private async Task<VersionSelection?> ShowVersionSelectionAsync(Schematic schematic)
    {
        MinecraftReleaseCatalog? catalog = null;
        string? catalogProblem = null;
        StatusText.Text = UiText.Get("Перевіряю список стабільних версій Minecraft…", "Checking the list of stable Minecraft releases…");
        try
        {
            using var catalogTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            catalog = await _resourceDownloader.GetStableReleaseCatalogAsync(cancellationToken: catalogTimeout.Token);
        }
        catch (ResourceDownloadException exception)
        {
            catalogProblem = exception.Message;
        }

        var releaseIds = catalog?.ReleaseIds.ToList() ?? [];
        const string knownVersion = "1.20.4";
        var suggestedVersion = schematic.DataVersion == 3700 ? knownVersion : catalog?.LatestReleaseId;
        if (!releaseIds.Contains(knownVersion, StringComparer.Ordinal))
        {
            releaseIds.Add(knownVersion);
        }
        if (suggestedVersion is not null && !releaseIds.Contains(suggestedVersion, StringComparer.Ordinal))
        {
            releaseIds.Insert(0, suggestedVersion);
        }

        var combo = new ComboBox
        {
            Header = UiText.Get("Стабільні версії", "Stable releases"),
            ItemsSource = releaseIds,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = suggestedVersion
        };
        var versionBox = new TextBox
        {
            Header = UiText.Get("Версія Minecraft для підтвердження", "Minecraft version to confirm"),
            PlaceholderText = UiText.Get("Наприклад, 1.20.4", "For example, 1.20.4"),
            MaxLength = 64,
            Text = suggestedVersion ?? string.Empty
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected)
            {
                versionBox.Text = selected;
            }
        };

        var dataVersionText = schematic.DataVersion is int value
            ? string.Format(UiText.Get("DataVersion у файлі: {0}.", "DataVersion in file: {0}."), value)
            : UiText.Get("У файлі немає DataVersion.", "The file has no DataVersion.");
        var versionNote = schematic.DataVersion == 3700
            ? UiText.Get("Це відома версія зі зразків проєкту. Перевірте й підтвердіть або виберіть іншу.", "This is the known version associated with the project's sample files. Confirm it or choose another.")
            : UiText.Get("Версію гри не вдалося визначити однозначно. Виберіть її вручну; останній реліз є лише пропозицією.", "The game version could not be identified. Choose it manually; the latest release is only a suggestion.");
        var catalogNote = catalogProblem is not null
            ? UiText.Get(
                $"Список версій не завантажився ({catalogProblem}). Можна ввести ідентифікатор вручну; для автоматичного завантаження він має бути стабільним релізом Mojang.",
                "The release list could not be loaded. Enter an ID manually; automatic download requires an official Mojang stable release.")
            : catalog?.IsCached == true
                ? UiText.Get("Показую останній список релізів із локального кешу.", "Showing the last release list saved in the local cache.")
                : string.Empty;

        var content = new StackPanel { Spacing = 10, MinWidth = 440 };
        var validationText = new TextBlock
        {
            Text = UiText.Get("Введіть або виберіть стабільний реліз із офіційного списку.", "Enter or choose a stable release from the official list."),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(new TextBlock { Text = dataVersionText, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = versionNote, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(catalogNote))
        {
            content.Children.Add(new TextBlock { Text = catalogNote, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        }
        content.Children.Add(combo);
        content.Children.Add(versionBox);
        content.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            Title = UiText.Get("Підтвердіть версію Minecraft", "Confirm Minecraft version"),
            Content = content,
            PrimaryButtonText = UiText.Get("Продовжити", "Continue"),
            CloseButtonText = UiText.Get("Скасувати", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootLayout.XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var selected = versionBox.Text.Trim();
            var missingFromOfficialList = catalog is { IsCached: false } &&
                !catalog.ReleaseIds.Contains(selected, StringComparer.Ordinal);
            if (!IsSafeVersionId(selected) || missingFromOfficialList)
            {
                validationText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var selectedVersion = versionBox.Text.Trim();

        var selectionNote = schematic.DataVersion == 3700 && selectedVersion == knownVersion
            ? string.Format(UiText.Get("DataVersion 3700 зіставлено зі зразками Minecraft {0}; версію підтверджено користувачем.", "DataVersion 3700 is associated with the Minecraft {0} sample files; the user confirmed the version."), selectedVersion)
            : string.Format(UiText.Get("Версію Minecraft {0} вибрано користувачем; DataVersion файлу не має підтвердженого зіставлення.", "Minecraft {0} was selected by the user; this file's DataVersion has no verified mapping."), selectedVersion);
        return new VersionSelection(selectedVersion, selectionNote);
    }

    private static bool IsSafeVersionId(string versionId) =>
        versionId.Length is > 0 and <= 64 && versionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private sealed record VersionSelection(string VersionId, string SelectionNote);

    private void FitScene_Click(object sender, RoutedEventArgs e)
    {
        if (_schematic is null)
        {
            return;
        }

        SceneViewport.Camera = CreateCamera(_schematic.Width, _schematic.Height, _schematic.Length);
        FitCameraToSchematic(_schematic);
    }

    private void FitCameraToSchematic(Schematic schematic)
    {
        if (SceneViewport.Camera is not PerspectiveCamera camera)
        {
            return;
        }

        var viewportWidth = (float)SceneViewport.ActualWidth;
        var viewportHeight = (float)SceneViewport.ActualHeight;
        var aspectRatio = viewportWidth > 0 && viewportHeight > 0 ? viewportWidth / viewportHeight : 1f;
        var verticalHalfFov = 22.5f * MathF.PI / 180f;
        var horizontalHalfFov = MathF.Atan(MathF.Tan(verticalHalfFov) * aspectRatio);
        var lookDirection = Vector3.Normalize(new Vector3(-1.2f, -0.9f, -1.3f));
        var rightDirection = Vector3.Normalize(Vector3.Cross(lookDirection, Vector3.UnitY));
        var cameraUpDirection = Vector3.Normalize(Vector3.Cross(rightDirection, lookDirection));
        var halfExtents = new Vector3(schematic.Width, schematic.Height, schematic.Length) * 0.5f;
        var halfWidth = ProjectedHalfExtent(rightDirection, halfExtents);
        var halfHeight = ProjectedHalfExtent(cameraUpDirection, halfExtents);
        var halfDepth = ProjectedHalfExtent(lookDirection, halfExtents);
        var distanceForWidth = halfWidth / MathF.Tan(horizontalHalfFov) + halfDepth;
        var distanceForHeight = halfHeight / MathF.Tan(verticalHalfFov) + halfDepth;
        var distance = Math.Max(8f, Math.Max(distanceForWidth, distanceForHeight) * 1.12f);
        var farPlaneMargin = Math.Max(10f, halfDepth * 0.05f);
        var center = Vector3.Zero;
        var position = center - lookDirection * distance;

        camera.Position = position;
        camera.LookDirection = center - position;
        camera.UpDirection = Vector3.UnitY;
        camera.FarPlaneDistance = Math.Max(camera.FarPlaneDistance, distance + halfDepth + farPlaneMargin);
    }

    private static float ProjectedHalfExtent(Vector3 axis, Vector3 halfExtents) =>
        MathF.Abs(axis.X) * halfExtents.X +
        MathF.Abs(axis.Y) * halfExtents.Y +
        MathF.Abs(axis.Z) * halfExtents.Z;

    private static PerspectiveCamera CreateCamera(int width, int height, int length)
    {
        var center = Vector3.Zero;
        var radius = Math.Max(8f, new Vector3(width, height, length).Length() * 1.45f);
        var position = center + Vector3.Normalize(new Vector3(1.2f, 0.9f, 1.3f)) * radius;
        return new PerspectiveCamera
        {
            Position = position,
            LookDirection = center - position,
            UpDirection = Vector3.UnitY,
            FieldOfView = 45
        };
    }
}
