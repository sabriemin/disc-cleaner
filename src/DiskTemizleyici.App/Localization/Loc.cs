using System.ComponentModel;
using System.Globalization;

namespace DiskTemizleyici.App.Localization;

/// <summary>
/// Tiny bilingual string table. XAML binds to it with <c>{Binding [Key], Source={x:Static l:Loc.I}}</c>
/// and every label updates when <see cref="Language"/> changes.
/// To add a language: add a column to <see cref="Strings"/> and an entry to <see cref="Languages"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[]
    {
        ("tr", "Türkçe"),
        ("en", "English"),
    };

    private string _language;

    private Loc()
    {
        _language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value || !Languages.Any(l => l.Code == value)) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language == "tr" ? "tr-TR" : "en-US");

    public string this[string key] =>
        Strings.TryGetValue(key, out var s) ? (_language == "tr" ? s.Tr : s.En) : $"[{key}]";

    public string F(string key, params object[] args) => string.Format(Culture, this[key], args);

    private static readonly Dictionary<string, (string Tr, string En)> Strings = new()
    {
        ["AppTitle"] = ("Disk Temizleyici", "Disk Cleaner"),

        // title bar & settings
        ["Settings"] = ("Ayarlar", "Settings"),
        ["Appearance"] = ("Görünüm", "Appearance"),
        ["StyleSolid"] = ("Düz", "Solid"),
        ["StyleGlass"] = ("Cam", "Glass"),
        ["GlassNeedsWin11"] = ("Cam görünüm için Windows 11 gerekir.", "Glass needs Windows 11."),
        ["AccentColor"] = ("Vurgu rengi", "Accent colour"),
        ["Language"] = ("Dil", "Language"),
        ["AdminTip"] = ("Yönetici olarak yeniden başlat. Windows klasörlerini taramak ve temizlemek için gerekir.", "Restart as administrator. Needed to scan and clean Windows folders."),
        ["Minimize"] = ("Simge durumuna küçült", "Minimise"),
        ["Maximize"] = ("Ekranı kapla", "Maximise"),
        ["Close"] = ("Kapat", "Close"),

        // island
        ["IslandReady"] = ("Hazır", "Ready"),
        ["IslandScanning"] = ("Taranıyor · {0:N0} dosya · {1}", "Scanning · {0:N0} files · {1}"),
        ["IslandDone"] = ("{0} tarandı · {1:0.0} sn", "{0} scanned · {1:0.0} s"),
        ["IslandCancelled"] = ("Tarama iptal edildi", "Scan cancelled"),
        ["IslandFailed"] = ("Tarama başarısız", "Scan failed"),
        ["IslandEvaluating"] = ("Öneriler hesaplanıyor…", "Finding things to clean…"),
        ["IslandCleaning"] = ("Temizleniyor · %{0:0}", "Cleaning · {0:0}%"),
        ["IslandCleaned"] = ("{0} boşaldı", "{0} freed"),

        // drive row
        ["Scan"] = ("Tara", "Scan"),
        ["ScanFolder"] = ("Klasör seç", "Pick folder"),
        ["Cancel"] = ("Durdur", "Stop"),
        ["DriveFree"] = ("{0} boş · {1}", "{0} free of {1}"),
        ["ScanDone"] = ("{0}  ·  {1}, {2:N0} dosya, {3:0.0} sn  ·  okunamayan klasör: {4:N0}", "{0}  ·  {1}, {2:N0} files, {3:0.0} s  ·  unreadable folders: {4:N0}"),
        ["ScanFailed"] = ("Tarama başarısız: {0}", "Scan failed: {0}"),

        // dock
        ["ViewFolders"] = ("Klasörler", "Folders"),
        ["ViewLargeFiles"] = ("Büyük dosyalar", "Large files"),
        ["ViewSuggestions"] = ("Öneriler", "Suggestions"),
        ["ReclaimableTip"] = ("Temizlenebilecek alan", "Space you can reclaim"),

        // folder view
        ["ColName"] = ("Ad", "Name"),
        ["ColSize"] = ("Boyut", "Size"),
        ["ColShare"] = ("Pay", "Share"),
        ["ColFiles"] = ("Dosya", "Files"),
        ["ColModified"] = ("Değiştirilme", "Modified"),
        ["CloudOnly"] = ("☁ {0} çevrimiçi", "☁ {0} online-only"),
        ["CloudOnlyTip"] = ("Bu dosyalar yalnızca bulutta (OneDrive, Telefon Bağlantısı…). Diskte yer kaplamazlar.", "These files live only in the cloud (OneDrive, Phone Link…). They use no disk space."),
        ["Link"] = ("bağlantı", "link"),
        ["AccessDenied"] = ("okunamadı", "no access"),
        ["FilesHere"] = ("Bu klasördeki dosyalar", "Files in this folder"),
        ["EmptyTitle"] = ("Diskinde neler var, görelim", "Let's see what fills your disk"),
        ["EmptyText"] = ("Yukarıdan bir sürücü seçip Tara'ya bas. Hiçbir şey sana sorulmadan silinmez.", "Pick a drive above and press Scan. Nothing is ever deleted without asking you."),
        ["ScanningTitle"] = ("Taranıyor", "Scanning"),
        ["FilesLabel"] = ("dosya", "files"),
        ["ReadLabel"] = ("okundu", "read"),
        ["LargeEmpty"] = ("En büyük dosyaları görmek için önce bir tarama yap.", "Run a scan to see the largest files."),

        // context menu
        ["OpenInExplorer"] = ("Gezgin'de göster", "Show in Explorer"),
        ["CopyPath"] = ("Yolu kopyala", "Copy path"),
        ["MoveToRecycle"] = ("Geri Dönüşüm Kutusu'na taşı", "Move to Recycle Bin"),
        ["DeletePermanent"] = ("Kalıcı olarak sil…", "Delete permanently…"),

        // suggestions
        ["RiskSafe"] = ("Güvenli", "Safe"),
        ["RiskCaution"] = ("Dikkat", "Caution"),
        ["NeedsAdmin"] = ("yönetici", "admin"),
        ["CandidateFiles"] = ("{0:N0} dosya", "{0:N0} files"),
        ["NoSuggestions"] = ("Temizlenecek bilinen bir şey bulunamadı. Diskin tertemiz.", "Nothing known to clean was found. Your disk is tidy."),
        ["SuggestionsHint"] = ("Açık olanlar temizlenir. \"Dikkat\" etiketlileri açmadan önce açıklamayı oku.", "Switched-on items get cleaned. Read the description before switching on \"Caution\" items."),
        ["RefreshSuggestions"] = ("Yeniden hesapla", "Recalculate"),
        ["SelectedLabel"] = ("Seçilen", "Selected"),
        ["CleanSelected"] = ("Temizle", "Clean up"),
        ["ModeRecycle"] = ("Geri Dönüşüm Kutusu", "Recycle Bin"),
        ["ModePermanent"] = ("Kalıcı", "Permanent"),
        ["ModeRecycleTip"] = ("Geri alınabilir. Yer, Geri Dönüşüm Kutusu boşaltılınca açılır.", "Can be undone. The space is freed when the Recycle Bin is emptied."),
        ["ModePermanentTip"] = ("Yer hemen boşalır. Geri alınamaz.", "Frees the space at once. Cannot be undone."),
        ["CleanDone"] = ("{0:N0} öğe silindi, {1:N0} öğe atlandı (kullanımda ya da izin yok).", "{0:N0} items deleted, {1:N0} skipped (in use or no permission)."),
        ["TreeStale"] = ("Güncel boyutlar için yeniden tara.", "Scan again for current sizes."),

        // dialogs
        ["ConfirmCleanTitle"] = ("Seçilenler temizlensin mi?", "Clean up the selected items?"),
        ["MoveToRecycleTitle"] = ("Geri Dönüşüm Kutusu'na taşınsın mı?", "Move to the Recycle Bin?"),
        ["DeletePermanentTitle"] = ("Kalıcı olarak silinsin mi?", "Delete permanently?"),
        ["ConfirmRecycle"] = ("{0:N0} öğe ({1}) Geri Dönüşüm Kutusu'na taşınacak. Gerekirse oradan geri alabilirsin.", "{0:N0} items ({1}) will be moved to the Recycle Bin. You can restore them from there."),
        ["ConfirmPermanent"] = ("{0:N0} öğe ({1}) kalıcı olarak silinecek. Bu işlem geri alınamaz.", "{0:N0} items ({1}) will be deleted permanently. This cannot be undone."),
        ["ConfirmEmptyBin"] = ("Geri Dönüşüm Kutusu da boşaltılacak ({0}); içindekiler geri getirilemez.", "The Recycle Bin will be emptied too ({0}); its contents cannot be restored."),
        ["BlockedTitle"] = ("Korunan konum", "Protected location"),
        ["Blocked"] = ("Bu konum Windows'un ya da kullanıcı klasörlerinin parçası olduğu için silinemez:\n{0}", "This location is part of Windows or your user folders, so it cannot be deleted:\n{0}"),
        ["Error"] = ("Bir sorun çıktı", "Something went wrong"),
        ["DialogCancel"] = ("Vazgeç", "Cancel"),
        ["DialogContinue"] = ("Devam et", "Continue"),
        ["DialogDelete"] = ("Sil", "Delete"),
        ["DialogOk"] = ("Tamam", "OK"),
    };
}
