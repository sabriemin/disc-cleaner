using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace DiskTemizleyici.App.Localization;

/// <summary>
/// Tiny bilingual string table. XAML binds to it with <c>{Binding [Key], Source={x:Static l:Loc.I}}</c>
/// and every label updates when <see cref="Language"/> changes.
/// <para>
/// Adding a language today means adding its text to every entry below (the table is a Turkish/English pair) and a
/// button to the language picker in MainWindow.xaml; moving the text to one file per language is planned.
/// English keys ending in <c>_one</c> are the singular forms used by <see cref="FN"/>.
/// </para>
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

    /// <summary>
    /// Number and date format: the user's own Windows regional settings when they match the app language,
    /// otherwise the standard format of the app language.
    /// </summary>
    public CultureInfo Culture =>
        CultureInfo.CurrentCulture.TwoLetterISOLanguageName == _language
            ? CultureInfo.CurrentCulture
            : CultureInfo.GetCultureInfo(_language == "tr" ? "tr-TR" : "en-US");

    public string this[string key]
    {
        get
        {
            if (Strings.TryGetValue(key, out var s)) return _language == "tr" ? s.Tr : s.En;
            Debug.Fail($"Missing translation key '{key}'");
            return key;
        }
    }

    public string F(string key, params object[] args) => string.Format(Culture, this[key], args);

    /// <summary>Like <see cref="F"/>, but uses the singular text ("1 file") when <paramref name="count"/> is 1.</summary>
    public string FN(string key, long count, params object[] args) =>
        string.Format(Culture, this[count == 1 && Strings.ContainsKey(key + "_one") ? key + "_one" : key], args);

    private static readonly Dictionary<string, (string Tr, string En)> Strings = new()
    {
        ["AppTitle"] = ("Disk Temizleyici", "Disk Cleaner"),

        // title bar & settings
        ["Settings"] = ("Ayarlar", "Settings"),
        ["Appearance"] = ("Görünüm", "Appearance"),
        ["StyleSolid"] = ("Düz", "Solid"),
        ["StyleGlass"] = ("Cam", "Glass"),
        ["GlassNeedsWin11"] = ("Cam görünüm için Windows 11 gerekir.", "Glass needs Windows 11."),
        ["AccentColor"] = ("Vurgu rengi", "Accent color"),
        ["Language"] = ("Dil", "Language"),
        ["VersionLabel"] = ("sürüm", "version"),
        ["AdminTip"] = ("Yönetici olarak yeniden başlat. Windows klasörlerini taramak ve temizlemek için gerekir.", "Restart as administrator. Needed to scan and clean Windows folders."),
        ["RunAsAdmin"] = ("Yönetici olarak yeniden başlat", "Restart as administrator"),
        ["Minimize"] = ("Simge durumuna küçült", "Minimize"),
        ["Maximize"] = ("Ekranı kapla", "Maximize"),
        ["Close"] = ("Kapat", "Close"),

        // island
        ["IslandReady"] = ("Hazır", "Ready"),
        ["IslandScanning"] = ("Taranıyor · {0:N0} dosya · {1}", "Scanning · {0:N0} files · {1}"),
        ["IslandScanning_one"] = ("Taranıyor · {0:N0} dosya · {1}", "Scanning · {0:N0} file · {1}"),
        ["IslandDone"] = ("{0} tarandı · {1:0.0} sn", "{0} scanned · {1:0.0} s"),
        ["IslandCancelled"] = ("Durduruldu", "Stopped"),
        ["IslandFailed"] = ("Tarama başarısız", "Scan failed"),
        ["IslandCleaning"] = ("Temizleniyor · %{0:0}", "Cleaning · {0:0}%"),
        ["IslandCleaned"] = ("{0} boşaldı", "{0} freed"),
        ["IslandMoved"] = ("{0} boşaldı · {1} Geri Dönüşüm Kutusu'nda", "{0} freed · {1} in the Recycle Bin"),
        ["IslandRescanning"] = ("Yeniden taranıyor…", "Rescanning…"),

        // drive row
        ["Scan"] = ("Tara", "Scan"),
        ["ScanFolder"] = ("Klasör seç", "Pick folder"),
        ["Cancel"] = ("Durdur", "Stop"),
        ["DriveFree"] = ("{0} boş · {1}", "{0} free of {1}"),
        ["ScanDone"] = ("{0}  ·  {1}, {2:N0} dosya, {3:0.0} sn  ·  okunamayan klasör: {4:N0}", "{0}  ·  {1}, {2:N0} files, {3:0.0} s  ·  unreadable folders: {4:N0}"),
        ["ScanDone_one"] = ("{0}  ·  {1}, {2:N0} dosya, {3:0.0} sn  ·  okunamayan klasör: {4:N0}", "{0}  ·  {1}, {2:N0} file, {3:0.0} s  ·  unreadable folders: {4:N0}"),
        ["DeniedHint"] = ("Okunamayan klasörleri de saymak için yönetici olarak yeniden başlat.", "Restart as administrator to include the folders that could not be read."),
        ["ScanFailed"] = ("Tarama başarısız: {0}", "Scan failed: {0}"),
        ["OtherUserWarning"] = ("Dikkat: uygulama \"{0}\" hesabıyla yönetici olarak çalışıyor, oturumdaki kullanıcı \"{1}\". Öneriler ve korunan klasörler bu hesaba göre belirlenir.", "Note: the app runs as administrator under the \"{0}\" account, but \"{1}\" is signed in. Suggestions and protected folders follow the \"{0}\" account."),

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
        ["Rescan"] = ("Bu klasörü yeniden tara", "Rescan this folder"),
        ["MoveToRecycle"] = ("Geri Dönüşüm Kutusu'na taşı", "Move to Recycle Bin"),
        ["DeletePermanent"] = ("Kalıcı olarak sil…", "Delete permanently…"),

        // suggestions
        ["RiskSafe"] = ("Güvenli", "Safe"),
        ["RiskCaution"] = ("Dikkat", "Caution"),
        ["NeedsAdmin"] = ("yönetici", "admin"),
        ["CandidateFiles"] = ("{0:N0} dosya", "{0:N0} files"),
        ["CandidateFiles_one"] = ("{0:N0} dosya", "{0:N0} file"),
        ["CandidateDenied"] = ("Bazı klasörleri okuma iznim yok; tamamını görmek için yönetici olarak yeniden başlat.", "Some folders could not be read; restart as administrator to see everything."),
        ["CandidateDeniedAdmin"] = ("Bazı klasörler yönetici olarak bile okunamadı; onlara dokunulmadı.", "Some folders could not be read even as administrator; they were left alone."),
        ["CandidateError"] = ("Kontrol edilemedi: {0}", "Could not be checked: {0}"),
        ["NoSuggestions"] = ("Temizlenecek bilinen bir şey bulunamadı. Diskin tertemiz.", "Nothing known to clean was found. Your disk is tidy."),
        ["SuggestionsHint"] = ("Açık olanlar temizlenir. \"Dikkat\" etiketlileri açmadan önce açıklamayı oku.", "Switched-on items get cleaned. Read the description before switching on \"Caution\" items."),
        ["EvaluatingHint"] = ("Temizlenebilecek yerler kontrol ediliyor… Bulunanlar hemen burada görünür; durdurmak için Esc.", "Checking what can be cleaned… Results appear here as they are found; press Esc to stop."),
        ["RuleProblems"] = ("Programın yanındaki rules.json dosyasında {0:N0} sorun var, o kurallar atlandı: {1}", "The rules.json next to the program has {0:N0} problems; those rules were skipped: {1}"),
        ["RuleProblems_one"] = ("Programın yanındaki rules.json dosyasında bir sorun var, o kural atlandı: {1}", "The rules.json next to the program has a problem; that rule was skipped: {1}"),
        ["RefreshSuggestions"] = ("Yeniden hesapla", "Recalculate"),
        ["SelectedLabel"] = ("Seçilen", "Selected"),
        ["CleanSelected"] = ("Temizle", "Clean up"),
        ["ModeRecycle"] = ("Geri Dönüşüm Kutusu", "Recycle Bin"),
        ["ModePermanent"] = ("Kalıcı", "Permanent"),
        ["ModeRecycleTip"] = ("Geri alınabilir. Yer, Geri Dönüşüm Kutusu boşaltılınca açılır.", "Can be undone. The space is freed when the Recycle Bin is emptied."),
        ["ModePermanentTip"] = ("Yer hemen boşalır. Geri alınamaz.", "Frees the space at once. Cannot be undone."),

        // results of a clean-up
        ["ResultDeleted"] = ("{0:N0} öğe temizlendi", "{0:N0} items cleaned"),
        ["ResultDeleted_one"] = ("{0:N0} öğe temizlendi", "{0:N0} item cleaned"),
        ["ResultInUse"] = ("{0:N0} kullanımda olduğu için atlandı", "{0:N0} skipped because in use"),
        ["ResultDenied"] = ("{0:N0} için izin yok", "{0:N0} without permission"),
        ["ResultBlocked"] = ("{0:N0} korunuyor", "{0:N0} protected"),
        ["ResultNotMoved"] = ("{0:N0} Geri Dönüşüm Kutusu'na taşınamadı", "{0:N0} could not be moved to the Recycle Bin"),
        ["ResultOther"] = ("{0:N0} başka bir hata", "{0:N0} other errors"),
        ["ResultOther_one"] = ("{0:N0} başka bir hata", "{0:N0} other error"),
        ["ResultChanged"] = ("{0:N0} öğe, öneri hazırlandıktan sonra değiştiği için bırakıldı", "{0:N0} left alone because they changed after the suggestion was made"),
        ["ResultChanged_one"] = ("{0:N0} öğe, öneri hazırlandıktan sonra değiştiği için bırakıldı", "{0:N0} left alone because it changed after the suggestion was made"),
        ["ResultBinFailed"] = ("Geri Dönüşüm Kutusu boşaltılamadı", "The Recycle Bin could not be emptied"),
        ["ResultBinSkipped"] = ("Geri Dönüşüm Kutusu boşaltılmadı", "The Recycle Bin was not emptied"),
        ["MovedHint"] = ("Geri Dönüşüm Kutusu'na taşınanlar, kutu boşaltılınca yer açar.", "Items in the Recycle Bin free their space when the bin is emptied."),

        // why something is protected
        ["BlockedTitle"] = ("Korunan konum", "Protected location"),
        ["Block_DriveRoot"] = ("Sürücünün kendisi silinemez.", "A whole drive cannot be deleted."),
        ["Block_SystemFolder"] = ("Bu klasör Windows'un ya da programların çalışması için gerekli.", "Windows or installed programs need this folder."),
        ["Block_WindowsFolder"] = ("Windows klasörünün içindekiler silinemez (yalnızca Temp, güncelleme indirmeleri ve çökme dökümleri temizlenebilir).", "Nothing inside the Windows folder can be deleted, except Temp, update downloads and crash dumps."),
        ["Block_RedirectedPath"] = ("Bu öğe artık temizlenen klasörün içinde değil: yolu bir bağlantıyla (junction ya da sembolik bağlantı) başka bir yere çevrilmiş. Dokunulmadı.", "This item is no longer inside the folder being cleaned: a junction or symbolic link now points its path somewhere else. It was left alone."),
        ["Block_UserFolder"] = ("Bu, bir kullanıcı hesabının ana klasörlerinden biri. Klasörün kendisi silinemez, içindekiler silinebilir.", "This is one of an account's main folders. The folder itself cannot be deleted; what is inside can."),
        ["Block_SyncedFolder"] = ("Bu klasör buluta eşitleniyor (ör. OneDrive). Silinirse bulutta ve tüm cihazlarında da silinir. Yer açmak için OneDrive'da \"Yer açın\" seçeneğini kullan.", "This folder is synced to the cloud (e.g. OneDrive). Deleting it would delete it in the cloud and on all your devices too. To free space, use \"Free up space\" in OneDrive."),
        ["Block_NetworkPath"] = ("Ağ konumlarındaki dosyalar silinmez.", "Files on network locations are not deleted."),
        ["Block_InvalidPath"] = ("Bu yol geçersiz.", "This path is not valid."),

        // dialogs
        ["ConfirmCleanTitle"] = ("Seçilenler temizlensin mi?", "Clean up the selected items?"),
        ["MoveToRecycleTitle"] = ("Geri Dönüşüm Kutusu'na taşınsın mı?", "Move to the Recycle Bin?"),
        ["DeletePermanentTitle"] = ("Kalıcı olarak silinsin mi?", "Delete permanently?"),
        ["ConfirmRecycle"] = ("{0:N0} öğe ({1}) Geri Dönüşüm Kutusu'na taşınacak. Gerekirse oradan geri alabilirsin.", "{0:N0} items ({1}) will be moved to the Recycle Bin. You can restore them from there."),
        ["ConfirmRecycle_one"] = ("{0:N0} öğe ({1}) Geri Dönüşüm Kutusu'na taşınacak. Gerekirse oradan geri alabilirsin.", "{0:N0} item ({1}) will be moved to the Recycle Bin. You can restore it from there."),
        ["ConfirmPermanent"] = ("{0:N0} öğe ({1}) kalıcı olarak silinecek. Bu işlem geri alınamaz.", "{0:N0} items ({1}) will be deleted permanently. This cannot be undone."),
        ["ConfirmPermanent_one"] = ("{0:N0} öğe ({1}) kalıcı olarak silinecek. Bu işlem geri alınamaz.", "{0:N0} item ({1}) will be deleted permanently. This cannot be undone."),
        ["ConfirmEmptyBin"] = ("Önce Geri Dönüşüm Kutusu boşaltılacak ({0}); içindekiler geri getirilemez. Bu temizlikte kutuya taşınanlar ise kutuda kalır.", "The Recycle Bin is emptied first ({0}); its contents cannot be restored. Items this clean-up moves to the bin stay there."),
        ["ConfirmCloudOnly"] = ("Bu klasörde ayrıca yalnızca bulutta duran {0:N0} dosya ({1}) var. Silinirse bunlar bulut hesabından ve diğer cihazlarından da silinir.", "This folder also holds {0:N0} online-only files ({1}). Deleting it removes them from the cloud account and your other devices too."),
        ["ConfirmCloudOnly_one"] = ("Bu klasörde ayrıca yalnızca bulutta duran {0:N0} dosya ({1}) var. Klasör silinirse bu dosya bulut hesabından ve diğer cihazlarından da silinir.", "This folder also holds {0:N0} online-only file ({1}). Deleting the folder removes it from the cloud account and your other devices too."),
        ["BinGrewTitle"] = ("Geri Dönüşüm Kutusu büyüdü", "The Recycle Bin has grown"),
        ["BinGrewText"] = ("Onayından sonra kutuya yeni öğeler girdi; kutu şimdi {0} tutuyor. Yine de boşaltılsın mı? İçindekiler geri getirilemez.", "New items went into the Recycle Bin after you confirmed; it now holds {0}. Empty it anyway? Its contents cannot be restored."),
        ["ConfirmSystemArea"] = ("Bu klasör Windows'a ya da Microsoft uygulamalarına ait veriler içerebilir. Silmek bazı programların ayarlarını sıfırlayabilir ya da onları bozabilir.", "This folder may hold data that Windows or Microsoft apps rely on. Deleting it can reset or break some programs."),
        ["Error"] = ("Bir sorun çıktı", "Something went wrong"),
        ["DialogCancel"] = ("Vazgeç", "Cancel"),
        ["DialogContinue"] = ("Devam et", "Continue"),
        ["DialogDelete"] = ("Sil", "Delete"),
        ["DialogOk"] = ("Tamam", "OK"),
        ["DialogClose"] = ("Kapat", "Close"),
        ["PathNotFound"] = ("Klasör bulunamadı: {0}", "Folder not found: {0}"),
        ["ResultCancelled"] = ("durduruldu", "stopped"),
        ["NoRecycleBinWarning"] = ("Bu sürücüde Geri Dönüşüm Kutusu yok (USB bellek, hafıza kartı ya da ağ sürücüsü); dosyalar kalıcı olarak silinecek.", "This drive has no Recycle Bin (USB stick, memory card or network drive); the files will be deleted permanently."),

        // protected files
        ["Block_SystemFile"] = ("Bu, Windows'un kendi yönettiği bir sistem dosyası ve elle silinemez. Hazırda bekletme dosyası (hiberfil.sys) \"powercfg /h off\" komutuyla kapatılır; sayfa dosyası (pagefile.sys) Sistem > Gelişmiş sistem ayarları > Performans'tan ayarlanır.", "This is a system file that Windows manages itself, so it cannot be deleted by hand. The hibernation file (hiberfil.sys) is turned off with \"powercfg /h off\"; the page file (pagefile.sys) is set in System > Advanced system settings > Performance."),
        ["Block_PreviousWindows"] = ("Önceki Windows kurulumunu Ayarlar > Sistem > Depolama > Geçici dosyalar'dan kaldır. Elle silmek \"Geri dön\" seçeneğini bozar ve içinde kalmış eski kişisel dosyaları kaybettirebilir.", "Remove the previous Windows installation in Settings > System > Storage > Temporary files. Deleting it by hand breaks \"Go back\" and can lose old personal files still inside it."),

        // whole-disk files
        ["Critical_LinuxDisk"] = ("Linux diski", "Linux disk"),
        ["Critical_VirtualDisk"] = ("Sanal makine diski", "Virtual machine disk"),
        ["Critical_SystemFile"] = ("Windows sistem dosyası", "Windows system file"),
        ["CriticalTitle"] = ("Bu dosya bütün bir disk", "This file is a whole disk"),
        ["CriticalLinuxText"] = ("{0}\n\nBu dosya bir WSL dağıtımının ya da Docker Desktop'ın diskinin tamamı. Silersen o ortamdaki bütün dosyalar, programlar ve veritabanları gider. Yer açmak için diski silmek yerine küçültmek gerekir: önce \"wsl --shutdown\" ile kapat, sonra diski sıkıştır.", "{0}\n\nThis file is the whole disk of a WSL distribution or of Docker Desktop. Deleting it removes every file, program and database in that environment. To free space, shrink the disk instead: shut it down with \"wsl --shutdown\", then compact the disk."),
        ["CriticalVmText"] = ("{0}\n\nBu dosya bir sanal makinenin diskinin tamamı. Silersen o makinedeki her şey gider. Artık kullanmıyorsan sanal makineyi kendi programından (Hyper-V, VirtualBox, VMware) kaldır.", "{0}\n\nThis file is the whole disk of a virtual machine. Deleting it removes everything in that machine. If you no longer use it, remove the machine in its own program (Hyper-V, VirtualBox, VMware)."),
        ["FolderHoldsLinuxDisks"] = ("Bu klasör WSL ya da Docker disklerini içeriyor; silinirse o ortamlardaki her şey gider.", "This folder holds WSL or Docker disks; deleting it removes everything in those environments."),
        ["DeleteAnyway"] = ("Yine de sil", "Delete anyway"),

        // unaccounted space, unreadable folders, cloud
        ["UnaccountedRow"] = ("Hesaba katılmayan alan (yaklaşık)", "Unaccounted space (approx.)"),
        ["UnaccountedTip"] = ("Sürücüde dolu görünen ama bu taramada sayılmayan alan. Olası nedenler: okunamayan klasörler, System Volume Information ve geri yükleme noktaları, NTFS'in kendi kayıtları, başka kullanıcıların dosyaları.", "Space the drive reports as used that this scan did not count. Likely reasons: folders that could not be read, System Volume Information and restore points, NTFS's own records, other users' files."),
        ["DeniedButton"] = ("{0:N0} klasör okunamadı · Yönetici olarak yeniden başlat", "{0:N0} folders could not be read · Restart as administrator"),
        ["DeniedButton_one"] = ("{0:N0} klasör okunamadı · Yönetici olarak yeniden başlat", "{0:N0} folder could not be read · Restart as administrator"),
        ["CloudOnlyTip"] = ("Bu dosyalar yalnızca bulutta (OneDrive, Telefon Bağlantısı…). Diskte yer kaplamazlar.", "These files live only in the cloud (OneDrive, Phone Link…). They use no disk space."),

        // file types
        ["ViewTypes"] = ("Türler", "File types"),
        ["TypesHint"] = ("Dosya uzantısına göre yaklaşık bir dağılım. Bir uzantıya tıklayınca Büyük dosyalar o türe süzülür.", "An approximate split by file extension. Click an extension to filter Large files to that type."),
        ["TypesEmpty"] = ("Dosya türlerini görmek için önce bir tarama yap.", "Run a scan to see the file types."),
        ["TypesStale"] = ("Son tam taramadan sonra dosyalar değişti; bu sayılar o taramaya ait. Güncel sayılar için yeniden tara (F5).", "Files changed after the last full scan, and these numbers are from that scan. Scan again (F5) for current numbers."),
        ["NoExtension"] = ("(uzantısız)", "(no extension)"),
        ["FilterLabel"] = ("Yalnızca {0}", "Only {0}"),
        ["ClearFilter"] = ("Süzgeci kaldır", "Clear filter"),
        ["FilterEmpty"] = ("En büyük dosyalar arasında bu türden dosya yok.", "None of the largest files is of this type."),
        ["Category_Video"] = ("Videolar", "Videos"),
        ["Category_Image"] = ("Görüntüler", "Images"),
        ["Category_Audio"] = ("Sesler", "Audio"),
        ["Category_Archive"] = ("Arşivler", "Archives"),
        ["Category_DiskImage"] = ("Disk görüntüleri ve sanal makineler", "Disk images and virtual machines"),
        ["Category_Installer"] = ("Programlar ve kurulum dosyaları", "Programs and installers"),
        ["Category_GameData"] = ("Oyun verileri", "Game data"),
        ["Category_Code"] = ("Kod ve derleme çıktısı", "Code and build output"),
        ["Category_Document"] = ("Belgeler", "Documents"),
        ["Category_Other"] = ("Diğer", "Other"),

        // crash report
        ["CrashTitle"] = ("Beklenmeyen bir hata oldu", "Something unexpected happened"),
        ["CrashText"] = ("Bu bilgisayara bir hata raporu kaydedildi; hiçbir şey gönderilmedi. Süren bir işlem varsa durduruldu.\n\n{0}", "An error report was saved on this computer; nothing was sent. Anything that was running has been stopped.\n\n{0}"),
        ["CrashTextNotSaved"] = ("Hata raporu kaydedilemedi (disk dolu olabilir); hiçbir şey gönderilmedi. Süren bir işlem varsa durduruldu.\n\n{0}", "The error report could not be saved (the disk may be full); nothing was sent. Anything that was running has been stopped.\n\n{0}"),
        ["CrashCopy"] = ("Raporu kopyala", "Copy report"),
        ["CrashOpenFolder"] = ("Klasörü aç", "Open folder"),
        ["CrashReportIssue"] = ("GitHub'da bildir", "Report on GitHub"),
    };
}
