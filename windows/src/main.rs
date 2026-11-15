#![windows_subsystem = "windows"]

use std::{
    env, fs,
    io::{self, Read, Write},
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::Command,
    ptr::{null, null_mut},
    sync::{
        atomic::{AtomicU32, AtomicUsize, Ordering},
        Mutex,
    },
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use windows_sys::Win32::{
    Foundation::*,
    Graphics::{Gdi::*, GdiPlus as gp},
    System::{Diagnostics::ToolHelp::*, LibraryLoader::GetModuleHandleW, Registry::*, Threading::*},
    UI::{HiDpi::GetDpiForWindow, Input::KeyboardAndMouse::*, Shell::SHCreateMemStream, WindowsAndMessaging::*},
};

const SCHEME: &str = "okakiiclient";
const BASE_URL: &str = "https://www.okakii.lol";
const DOWNLOAD_BASE: &str = "https://okakii.lol/download";
const EXE_NAME: &str = "OkakiiPlayerBeta.exe";
// Bump together with OkakiiLauncher.version on the site whenever a new launcher ships.
const LAUNCHER_VERSION: i32 = 11;
const UPDATED_FLAG: &str = "--updated";
// Bump when the site starts serving fixed versions of assets players may already have cached.
const ASSET_CACHE_EPOCH: &str = "1";

const W: i32 = 520;
const H: i32 = 320;
const TIMER_MARQUEE: usize = 1;
const TIMER_POLL: usize = 2;
const WM_MOUSELEAVE: u32 = 0x02A3;

struct Ui {
    status: String,
    progress: i32,
    marquee: f32,
    success: bool,
    hover_ok: bool,
}

static UI: Mutex<Ui> = Mutex::new(Ui { status: String::new(), progress: -1, marquee: 0.0, success: false, hover_ok: false });
static HWND_G: AtomicUsize = AtomicUsize::new(0);
static DPI: AtomicU32 = AtomicU32::new(96);
static LOGO: AtomicUsize = AtomicUsize::new(0);

struct Job {
    place: String,
    ticket: String,
    year: &'static str,
}

fn okakii_dir() -> PathBuf {
    PathBuf::from(env::var_os("LOCALAPPDATA").unwrap_or_default()).join("Okakii")
}
fn clients_root() -> PathBuf {
    okakii_dir().join("Clients")
}
fn guard_dir() -> PathBuf {
    okakii_dir().join("AntiCheat")
}
fn launcher_path() -> PathBuf {
    okakii_dir().join("OkakiiLauncher.exe")
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}
fn hwnd() -> HWND {
    HWND_G.load(Ordering::Relaxed) as HWND
}
fn sc(v: i32) -> i32 {
    v * DPI.load(Ordering::Relaxed) as i32 / 96
}
fn ticks() -> u128 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0)
}

fn set_status(s: impl Into<String>) {
    UI.lock().unwrap().status = s.into();
    unsafe { InvalidateRect(hwnd(), null(), 0) };
}
fn set_progress(p: i32) {
    UI.lock().unwrap().progress = p;
    unsafe { InvalidateRect(hwnd(), null(), 0) };
}
fn close() {
    unsafe { PostMessageW(hwnd(), WM_CLOSE, 0, 0) };
}
fn message(text: &str, caption: &str, flags: u32) {
    unsafe { MessageBoxW(hwnd(), wide(text).as_ptr(), wide(caption).as_ptr(), flags) };
}
fn error_and_close(text: &str) {
    message(text, "Okakii - Error", MB_OK | MB_ICONERROR);
    close();
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        let hex = |c: u8| (c as char).to_digit(16);
        if b[i] == b'%' && i + 2 < b.len() {
            if let (Some(h), Some(l)) = (hex(b[i + 1]), hex(b[i + 2])) {
                out.push((h * 16 + l) as u8);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn percent_encode(s: &str) -> String {
    s.bytes()
        .map(|c| match c {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => (c as char).to_string(),
            _ => format!("%{c:02X}"),
        })
        .collect()
}

// The link comes from any web page, and its values end up on command lines and in a cookie.
fn parse_link(link: &str) -> Option<Job> {
    let scheme = format!("{SCHEME}://");
    if !link.get(..scheme.len())?.eq_ignore_ascii_case(&scheme) {
        return None;
    }
    let (mut place, mut ticket, mut year) = (String::new(), String::new(), String::new());
    for kv in link.split_once('?')?.1.split('&') {
        let Some((k, v)) = kv.split_once('=') else { continue };
        let v = percent_decode(v.trim_end_matches('/'));
        match k {
            "place" => place = v,
            "ticket" => ticket = v,
            "year" => year = v,
            _ => {}
        }
    }
    let ok_ticket = !ticket.is_empty() && ticket.chars().all(|c| c.is_ascii_graphic() && !matches!(c, '"' | '&' | '#'));
    if place.is_empty() || !place.bytes().all(|c| c.is_ascii_digit()) || !ok_ticket {
        return None;
    }
    let year = match year.as_str() {
        "2021" => "2021",
        "2020" => "2020",
        "2018" => "2018",
        "2015" => "2015",
        _ => "2017",
    };
    Some(Job { place, ticket, year })
}

fn get_text(url: &str, timeout: Duration) -> Option<String> {
    let body = ureq::get(url).timeout(timeout).call().ok()?.into_string().ok()?;
    Some(body.trim().to_string())
}

fn download(url: &str, dest: &Path, show_progress: bool) -> Result<(), Box<dyn std::error::Error>> {
    let resp = ureq::get(url).call()?;
    let total: u64 = resp.header("Content-Length").and_then(|v| v.parse().ok()).unwrap_or(0);
    let mut reader = resp.into_reader();
    let mut file = io::BufWriter::with_capacity(1 << 20, fs::File::create(dest)?);
    let mut buf = vec![0u8; 1 << 16];
    let (mut got, mut last) = (0u64, u64::MAX);
    loop {
        let n = reader.read(&mut buf)?;
        if n == 0 {
            break;
        }
        file.write_all(&buf[..n])?;
        got += n as u64;
        if !show_progress {
            continue;
        }
        let shown = if total > 0 { got * 100 / total } else { got >> 20 };
        if shown != last {
            last = shown;
            if total > 0 {
                set_status(format!("Downloading Okakii ... {shown}%"));
                set_progress(shown as i32);
            } else {
                set_status(format!("Downloading Okakii ... {shown} MB"));
            }
        }
    }
    file.flush()?;
    Ok(())
}

fn extract(zip_path: &Path, dest: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let mut zip = zip::ZipArchive::new(io::BufReader::new(fs::File::open(zip_path)?))?;
    for i in 0..zip.len() {
        let mut entry = zip.by_index(i)?;
        let Some(rel) = entry.enclosed_name() else { continue };
        let out = dest.join(rel);
        if entry.is_dir() {
            fs::create_dir_all(&out)?;
            continue;
        }
        if let Some(parent) = out.parent() {
            fs::create_dir_all(parent)?;
        }
        io::copy(&mut entry, &mut io::BufWriter::with_capacity(1 << 20, fs::File::create(&out)?))?;
    }
    Ok(())
}

fn pids(exe: &str) -> Vec<u32> {
    let mut found = Vec::new();
    unsafe {
        let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snap == INVALID_HANDLE_VALUE {
            return found;
        }
        let mut pe: PROCESSENTRY32W = std::mem::zeroed();
        pe.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut more = Process32FirstW(snap, &mut pe) != 0;
        while more {
            let len = pe.szExeFile.iter().position(|&c| c == 0).unwrap_or(pe.szExeFile.len());
            if String::from_utf16_lossy(&pe.szExeFile[..len]).eq_ignore_ascii_case(exe) {
                found.push(pe.th32ProcessID);
            }
            more = Process32NextW(snap, &mut pe) != 0;
        }
        CloseHandle(snap);
    }
    found
}

fn kill_all(exe: &str) {
    for pid in pids(exe) {
        unsafe {
            let h = OpenProcess(PROCESS_TERMINATE | PROCESS_SYNCHRONIZE, 0, pid);
            if !h.is_null() {
                TerminateProcess(h, 1);
                WaitForSingleObject(h, 3000);
                CloseHandle(h);
            }
        }
    }
}

fn ensure_existence() {
    let dir = okakii_dir();
    let _ = fs::create_dir_all(&dir);
    let _ = fs::remove_file(dir.join("OkakiiLauncher.old.exe"));
    let launcher = launcher_path();
    let Ok(me) = env::current_exe() else { return };
    let modified = |p: &Path| fs::metadata(p).and_then(|m| m.modified()).ok();
    if !launcher.exists() || modified(&me) > modified(&launcher) {
        let _ = fs::copy(&me, &launcher);
    }
}

fn reg_set(key: HKEY, name: &str, value: &str) -> bool {
    let data = wide(value);
    let name = if name.is_empty() { None } else { Some(wide(name)) };
    unsafe {
        RegSetValueExW(key, name.as_ref().map_or(null(), |n| n.as_ptr()), 0, REG_SZ, data.as_ptr().cast(), (data.len() * 2) as u32) == 0
    }
}

fn reg_create(path: &str) -> Option<HKEY> {
    let mut key: HKEY = null_mut();
    let rc = unsafe { RegCreateKeyExW(HKEY_CURRENT_USER, wide(path).as_ptr(), 0, null(), 0, KEY_WRITE, null(), &mut key, null_mut()) };
    (rc == 0).then_some(key)
}

fn register_protocol() {
    let root = format!(r"Software\Classes\{SCHEME}");
    unsafe {
        let mut existing: HKEY = null_mut();
        if RegOpenKeyExW(HKEY_CURRENT_USER, wide(&root).as_ptr(), 0, KEY_READ, &mut existing) == 0 {
            RegCloseKey(existing);
            return;
        }
        let command = format!("\"{}\" \"%1\"", launcher_path().display());
        let ok = match (reg_create(&root), reg_create(&format!(r"{root}\shell\open\command"))) {
            (Some(k), Some(cmd)) => {
                let ok = reg_set(k, "", "URL:Okakii Protocol") && reg_set(k, "URL Protocol", "") && reg_set(cmd, "", &command);
                RegCloseKey(k);
                RegCloseKey(cmd);
                ok
            }
            (k, cmd) => {
                k.map(|k| RegCloseKey(k));
                cmd.map(|k| RegCloseKey(k));
                false
            }
        };
        if !ok {
            message("Could not register okakiiclient:// protocol.", "Okakii", MB_OK | MB_ICONWARNING);
        }
    }
}

fn remove_old_guard() {
    kill_all("OkakiiAntiCheat.exe");
    let _ = fs::remove_dir_all(okakii_dir().join("Guard"));
}

fn try_self_update() -> bool {
    let args: Vec<String> = env::args().skip(1).collect();
    if args.iter().any(|a| a == UPDATED_FLAG) {
        return false;
    }
    let Some(remote) = get_text(&format!("{DOWNLOAD_BASE}/OkakiiLauncher.version"), Duration::from_secs(5))
        .and_then(|t| t.parse::<i32>().ok())
        .filter(|&v| v > LAUNCHER_VERSION)
    else {
        return false;
    };
    set_status("Updating Okakii ...");
    let launcher = launcher_path();
    let fresh = launcher.with_file_name("OkakiiLauncher.new.exe");
    let old = launcher.with_file_name("OkakiiLauncher.old.exe");
    let swap = || -> io::Result<()> {
        download(&format!("{DOWNLOAD_BASE}/OkakiiLauncher.exe?v={remote}"), &fresh, false).map_err(|e| io::Error::other(e.to_string()))?;
        let _ = fs::remove_file(&old);
        let moved_old = launcher.exists() && fs::rename(&launcher, &old).is_ok();
        if let Err(e) = fs::rename(&fresh, &launcher) {
            if moved_old && !launcher.exists() {
                let _ = fs::rename(&old, &launcher);
            }
            return Err(e);
        }
        Command::new(&launcher).args(&args).arg(UPDATED_FLAG).spawn().map(|_| ())
    };
    if swap().is_err() {
        let _ = fs::remove_file(&fresh);
        return false;
    }
    true
}

fn version_file(year: &str) -> PathBuf {
    clients_root().join(year).join(".okakii_version")
}

fn resolve_client_exe(year: &str) -> PathBuf {
    let per_user = clients_root().join(year).join(EXE_NAME);
    let downloads = PathBuf::from(env::var_os("USERPROFILE").unwrap_or_default()).join("Downloads").join("Clients").join(year).join(EXE_NAME);
    if !per_user.exists() && downloads.exists() { downloads } else { per_user }
}

fn install_client(year: &str, version: Option<&str>) -> Result<(), Box<dyn std::error::Error>> {
    set_status("Downloading Okakii ...");
    set_progress(0);
    fs::create_dir_all(clients_root())?;
    let v = version.map(str::to_string).unwrap_or_else(|| ticks().to_string());
    let url = format!("{DOWNLOAD_BASE}/OkakiiClient{year}.zip?v={}", percent_encode(&v));
    let tmp = env::temp_dir().join(format!("OkakiiClient{year}.{:x}.zip", ticks()));
    let result = download(&url, &tmp, true).and_then(|_| {
        set_status("Installing Okakii ...");
        set_progress(-1);
        extract(&tmp, &clients_root())
    });
    let _ = fs::remove_file(&tmp);
    result
}

fn clear_stale_asset_cache() {
    let marker = okakii_dir().join(".asset-cache-epoch");
    if fs::read_to_string(&marker).is_ok_and(|s| s.trim() == ASSET_CACHE_EPOCH) {
        return;
    }
    if !pids("OkakiiPlayerBeta.exe").is_empty() || !pids("RobloxPlayerBeta.exe").is_empty() {
        return;
    }
    if let Ok(files) = fs::read_dir(env::temp_dir().join("Roblox").join("http")) {
        for f in files.flatten().filter(|f| f.path().is_file()) {
            let _ = fs::remove_file(f.path());
        }
    }
    let _ = fs::create_dir_all(okakii_dir());
    let _ = fs::write(marker, ASSET_CACHE_EPOCH);
}

fn ensure_guard() {
    let dir = guard_dir();
    let exe = dir.join("OkakiiGuard.exe");
    let ver = dir.join(".version");
    let Some(remote) = get_text(&format!("{DOWNLOAD_BASE}/OkakiiGuard.version?t={}", ticks()), Duration::from_secs(5)) else { return };
    if exe.exists() && fs::read_to_string(&ver).is_ok_and(|l| l.trim() == remote) {
        return;
    }
    set_status("Updating anti-cheat ...");
    let tmp = dir.join("OkakiiGuard.exe.new");
    let _ = (|| -> Result<(), Box<dyn std::error::Error>> {
        fs::create_dir_all(&dir)?;
        download(&format!("{DOWNLOAD_BASE}/OkakiiGuard.exe?v={}", percent_encode(&remote)), &tmp, false)?;
        kill_all("OkakiiGuard.exe");
        fs::copy(&tmp, &exe)?;
        fs::remove_file(&tmp)?;
        fs::write(&ver, &remote)?;
        Ok(())
    })();
}

// Flag the PC session first: if the guard is deleted or fails to start, the server's heartbeat
// check kicks the player instead of treating them like a mobile/Mac player.
fn start_guard(job: &Job, game_pid: u32, client_dir: &Path) {
    let _ = ureq::post(&format!("{BASE_URL}/guard/v1/pc-session"))
        .timeout(Duration::from_secs(5))
        .set("Content-Type", "application/json")
        .set("Cookie", &format!(".ROBLOSECURITY={}", job.ticket))
        .send_string("{}");
    let dir = guard_dir();
    let exe = dir.join("OkakiiGuard.exe");
    if !exe.exists() {
        return;
    }
    let args = format!(
        "--parent {game_pid} --ticket \"{}\" --place {} --year {} --clientdir \"{}\" --launcher {}",
        job.ticket,
        job.place,
        job.year,
        client_dir.display(),
        std::process::id()
    );
    let _ = Command::new(exe).raw_arg(args).creation_flags(CREATE_NO_WINDOW).current_dir(dir).spawn();
}

fn focus_game_window(pid: u32) {
    unsafe extern "system" fn find(h: HWND, lp: LPARAM) -> BOOL {
        let state = &mut *(lp as *mut (u32, HWND));
        let mut owner = 0u32;
        GetWindowThreadProcessId(h, &mut owner);
        if owner == state.0 && IsWindowVisible(h) != 0 && GetWindow(h, GW_OWNER).is_null() {
            state.1 = h;
            return 0;
        }
        1
    }
    for _ in 0..40 {
        let mut state: (u32, HWND) = (pid, null_mut());
        unsafe { EnumWindows(Some(find), &mut state as *mut _ as LPARAM) };
        if !state.1.is_null() {
            unsafe { SetForegroundWindow(state.1) };
            return;
        }
        thread::sleep(Duration::from_millis(100));
    }
}

fn launch_client(job: &Job, exe: &Path) {
    set_status("Starting Okakii ...");
    let year_flag = match job.year {
        "2021" | "2020" | "2018" => format!("&{}=true", job.year),
        _ => String::new(),
    };
    let join = format!("{BASE_URL}/game/PlaceLauncher.ashx?placeid={}&ticket={}{year_flag}", job.place, job.ticket);
    let args = format!("-a \"{BASE_URL}/Login/Negotiate.ashx\" -j \"{join}\" -t \"{}\"", job.ticket);
    let client_dir = exe.parent().unwrap_or(Path::new("."));
    let mut child = match Command::new(exe).raw_arg(args).current_dir(client_dir).spawn() {
        Ok(c) => c,
        Err(_) => return error_and_close("Failed to launch the client."),
    };
    let pid = child.id();
    start_guard(job, pid, client_dir);
    focus_game_window(pid);
    thread::sleep(Duration::from_millis(800));

    // Stay alive hidden as a watchdog: if the guard is killed while the game runs, relaunch it.
    unsafe { ShowWindowAsync(hwnd(), SW_HIDE) };
    while matches!(child.try_wait(), Ok(None)) {
        if pids("OkakiiGuard.exe").is_empty() {
            ensure_guard();
            start_guard(job, pid, client_dir);
        }
        thread::sleep(Duration::from_secs(4));
    }
    close();
}

fn launch(job: Job) {
    if try_self_update() {
        return close();
    }
    let exe = resolve_client_exe(job.year);
    let remote = get_text(&format!("{DOWNLOAD_BASE}/OkakiiClient{}.version", job.year), Duration::from_secs(30));
    let local = fs::read_to_string(version_file(job.year)).ok().map(|s| s.trim().to_string());
    if !exe.exists() || (remote.is_some() && local != remote) {
        if let Err(e) = install_client(job.year, remote.as_deref()) {
            return error_and_close(&format!("Error: {e}"));
        }
        if let Some(v) = &remote {
            let _ = fs::write(version_file(job.year), v);
        }
    }
    let exe = resolve_client_exe(job.year);
    if !exe.exists() {
        return error_and_close(&format!("Couldn't install the client.\nExpected:\n{}", exe.display()));
    }
    clear_stale_asset_cache();
    ensure_guard();
    launch_client(&job, &exe);
}

fn load_logo() {
    let png = include_bytes!("../../assets/logo.png");
    unsafe {
        let mut token = 0usize;
        let input = gp::GdiplusStartupInput { GdiplusVersion: 1, DebugEventCallback: 0, SuppressBackgroundThread: 0, SuppressExternalCodecs: 0 };
        if gp::GdiplusStartup(&mut token, &input, null_mut()) != gp::Ok {
            return;
        }
        let stream = SHCreateMemStream(png.as_ptr(), png.len() as u32);
        let mut bmp: *mut gp::GpBitmap = null_mut();
        if !stream.is_null() && gp::GdipCreateBitmapFromStream(stream, &mut bmp) == gp::Ok {
            LOGO.store(bmp as usize, Ordering::Relaxed);
        }
    }
}

fn font(px: i32, weight: i32, face: &str) -> HFONT {
    unsafe { CreateFontW(-sc(px), 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET.into(), 0, 0, CLEARTYPE_QUALITY.into(), 0, wide(face).as_ptr()) }
}

fn ok_button() -> RECT {
    RECT { left: sc(W / 2 - 60), top: sc(H - 50), right: sc(W / 2 + 60), bottom: sc(H - 16) }
}

fn draw_text(dc: HDC, text: &str, mut r: RECT, f: HFONT, color: COLORREF) {
    unsafe {
        let old = SelectObject(dc, f);
        SetTextColor(dc, color);
        let t: Vec<u16> = text.encode_utf16().collect();
        DrawTextW(dc, t.as_ptr(), t.len() as i32, &mut r, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        SelectObject(dc, old);
        DeleteObject(f);
    }
}

fn fill(dc: HDC, r: RECT, color: COLORREF) {
    unsafe {
        let b = CreateSolidBrush(color);
        FillRect(dc, &r, b);
        DeleteObject(b);
    }
}

const fn rgb(r: u8, g: u8, b: u8) -> COLORREF {
    r as u32 | (g as u32) << 8 | (b as u32) << 16
}

fn paint(dc: HDC, w: i32, h: i32) {
    let ui = UI.lock().unwrap();
    unsafe {
        fill(dc, RECT { left: 0, top: 0, right: w, bottom: h }, rgb(255, 255, 255));
        let pen = CreatePen(PS_SOLID, 1, rgb(200, 200, 200));
        let old_pen = SelectObject(dc, pen);
        let old_brush = SelectObject(dc, GetStockObject(NULL_BRUSH));
        Rectangle(dc, 0, 0, w, h);
        SelectObject(dc, old_pen);
        SelectObject(dc, old_brush);
        DeleteObject(pen);
        SetBkMode(dc, TRANSPARENT as i32);

        let logo = LOGO.load(Ordering::Relaxed) as *mut gp::GpImage;
        if !logo.is_null() {
            let (mut iw, mut ih) = (0u32, 0u32);
            gp::GdipGetImageWidth(logo, &mut iw);
            gp::GdipGetImageHeight(logo, &mut ih);
            let size = sc(104);
            let (lw, lh) = if iw > ih { (size, size * ih as i32 / iw as i32) } else { (size * iw as i32 / ih.max(1) as i32, size) };
            let mut g: *mut gp::GpGraphics = null_mut();
            if gp::GdipCreateFromHDC(dc, &mut g) == gp::Ok {
                gp::GdipSetInterpolationMode(g, gp::InterpolationModeHighQualityBicubic);
                gp::GdipDrawImageRectI(g, logo, (w - lw) / 2, sc(58) + (size - lh) / 2, lw, lh);
                gp::GdipDeleteGraphics(g);
            }
        }

        if ui.success {
            draw_text(dc, "OKAKII IS SUCCESSFULLY INSTALLED!", RECT { left: 0, top: sc(178), right: w, bottom: sc(210) }, font(24, 300, "Segoe UI Light"), rgb(25, 25, 25));
            draw_text(dc, "Click the 'Play' button on any game to join the action!", RECT { left: 0, top: sc(210), right: w, bottom: sc(236) }, font(15, 400, "Segoe UI"), rgb(25, 25, 25));
            let b = ok_button();
            let brush = CreateSolidBrush(if ui.hover_ok { rgb(63, 198, 121) } else { rgb(2, 183, 87) });
            let old_brush = SelectObject(dc, brush);
            let old_pen = SelectObject(dc, GetStockObject(NULL_PEN));
            RoundRect(dc, b.left, b.top, b.right + 1, b.bottom + 1, sc(6), sc(6));
            SelectObject(dc, old_brush);
            SelectObject(dc, old_pen);
            DeleteObject(brush);
            draw_text(dc, "Ok", b, font(18, 400, "Segoe UI"), rgb(255, 255, 255));
            return;
        }

        draw_text(dc, &ui.status, RECT { left: sc(20), top: sc(196), right: w - sc(20), bottom: sc(222) }, font(15, 400, "Segoe UI"), rgb(34, 34, 34));

        let bar = RECT { left: sc(30), top: sc(236), right: w - sc(30), bottom: sc(256) };
        fill(dc, bar, rgb(230, 230, 230));
        let inner = RECT { left: bar.left + 1, top: bar.top + 1, right: bar.right - 1, bottom: bar.bottom - 1 };
        let iw = inner.right - inner.left;
        let (x0, x1) = if ui.progress >= 0 {
            (inner.left, inner.left + iw * ui.progress.min(100) / 100)
        } else {
            let x = inner.left + (iw as f32 * ui.marquee) as i32;
            (x.max(inner.left), (x + iw * 28 / 100).min(inner.right))
        };
        if x1 > x0 {
            let rows = inner.bottom - inner.top;
            for y in 0..rows {
                let t = y as f32 / (rows - 1).max(1) as f32;
                let mix = |a: u8, b: u8| (a as f32 + (b as f32 - a as f32) * t) as u8;
                fill(dc, RECT { left: x0, top: inner.top + y, right: x1, bottom: inner.top + y + 1 }, rgb(mix(18, 6), mix(138, 112), mix(18, 6)));
            }
        }
        let pen = CreatePen(PS_SOLID, 1, rgb(188, 188, 188));
        let old_pen = SelectObject(dc, pen);
        let old_brush = SelectObject(dc, GetStockObject(NULL_BRUSH));
        Rectangle(dc, bar.left, bar.top, bar.right, bar.bottom);
        SelectObject(dc, old_pen);
        SelectObject(dc, old_brush);
        DeleteObject(pen);
    }
}

fn in_ok(lp: LPARAM) -> bool {
    let (x, y) = ((lp & 0xFFFF) as i16 as i32, ((lp >> 16) & 0xFFFF) as i16 as i32);
    let b = ok_button();
    UI.lock().unwrap().success && x >= b.left && x < b.right && y >= b.top && y < b.bottom
}

unsafe extern "system" fn wndproc(h: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_ERASEBKGND => return 1,
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = std::mem::zeroed();
            let dc = BeginPaint(h, &mut ps);
            let mut rc: RECT = std::mem::zeroed();
            GetClientRect(h, &mut rc);
            let mem = CreateCompatibleDC(dc);
            let bmp = CreateCompatibleBitmap(dc, rc.right, rc.bottom);
            let old = SelectObject(mem, bmp);
            paint(mem, rc.right, rc.bottom);
            BitBlt(dc, 0, 0, rc.right, rc.bottom, mem, 0, 0, SRCCOPY);
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(h, &ps);
            return 0;
        }
        WM_TIMER if wp == TIMER_MARQUEE => {
            let mut ui = UI.lock().unwrap();
            if ui.progress < 0 && !ui.success {
                ui.marquee += 0.012;
                if ui.marquee > 1.0 {
                    ui.marquee = -0.28;
                }
                InvalidateRect(h, null(), 0);
            }
            return 0;
        }
        // Like the classic bootstrapper: once installed, the window closes itself when a game starts.
        WM_TIMER if wp == TIMER_POLL => {
            if !pids(EXE_NAME).is_empty() {
                DestroyWindow(h);
            }
            return 0;
        }
        WM_KEYDOWN => {
            let key = wp as u16;
            if key == VK_ESCAPE || (key == VK_RETURN && UI.lock().unwrap().success) {
                DestroyWindow(h);
            }
            return 0;
        }
        WM_LBUTTONDOWN => {
            if !in_ok(lp) {
                ReleaseCapture();
                SendMessageW(h, WM_NCLBUTTONDOWN, HTCAPTION as usize, 0);
            }
            return 0;
        }
        WM_LBUTTONUP => {
            if in_ok(lp) {
                DestroyWindow(h);
            }
            return 0;
        }
        WM_MOUSEMOVE | WM_MOUSELEAVE => {
            let over = msg == WM_MOUSEMOVE && in_ok(lp);
            let mut ui = UI.lock().unwrap();
            if ui.hover_ok != over {
                ui.hover_ok = over;
                InvalidateRect(h, null(), 0);
            }
            let mut tme = TRACKMOUSEEVENT { cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32, dwFlags: TME_LEAVE, hwndTrack: h, dwHoverTime: 0 };
            if msg == WM_MOUSEMOVE {
                TrackMouseEvent(&mut tme);
            }
            return 0;
        }
        WM_SETCURSOR if UI.lock().unwrap().hover_ok => {
            SetCursor(LoadCursorW(null_mut(), IDC_HAND));
            return 1;
        }
        WM_DPICHANGED => {
            DPI.store((wp & 0xFFFF) as u32, Ordering::Relaxed);
            let r = &*(lp as *const RECT);
            SetWindowPos(h, null_mut(), r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOZORDER | SWP_NOACTIVATE);
            return 0;
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            return 0;
        }
        _ => {}
    }
    DefWindowProcW(h, msg, wp, lp)
}

fn main() {
    let args: Vec<String> = env::args().skip(1).collect();
    let preview = args.iter().any(|a| a == "--preview");
    let job = if preview { None } else { args.first().and_then(|a| parse_link(a)) };
    let success = if preview { args.iter().any(|a| a == "--done") } else { job.is_none() };

    if !preview {
        ensure_existence();
        register_protocol();
    }
    UI.lock().unwrap().status = "Connecting to Okakii ...".into();
    UI.lock().unwrap().success = success;
    load_logo();

    unsafe {
        let instance = GetModuleHandleW(null());
        let class = wide("OkakiiLauncher");
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW | CS_DROPSHADOW,
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        };
        RegisterClassW(&wc);
        let h = CreateWindowExW(WS_EX_APPWINDOW, class.as_ptr(), wide("Okakii").as_ptr(), WS_POPUP, 0, 0, W, H, null_mut(), null_mut(), instance, null());
        if h.is_null() {
            return;
        }
        HWND_G.store(h as usize, Ordering::Relaxed);
        DPI.store(GetDpiForWindow(h).max(96), Ordering::Relaxed);
        let (w, ht) = (sc(W), sc(H));
        SetWindowPos(h, null_mut(), (GetSystemMetrics(SM_CXSCREEN) - w) / 2, (GetSystemMetrics(SM_CYSCREEN) - ht) / 2, w, ht, SWP_NOZORDER);

        let logo = LOGO.load(Ordering::Relaxed) as *mut gp::GpBitmap;
        let mut icon: HICON = null_mut();
        if !logo.is_null() && gp::GdipCreateHICONFromBitmap(logo, &mut icon) == gp::Ok {
            SendMessageW(h, WM_SETICON, ICON_BIG as usize, icon as isize);
            SendMessageW(h, WM_SETICON, ICON_SMALL as usize, icon as isize);
        }

        ShowWindow(h, SW_SHOWNORMAL);
        SetForegroundWindow(h);
        SetTimer(h, TIMER_MARQUEE, 16, None);

        if !preview {
            thread::spawn(remove_old_guard);
            match job {
                Some(job) => drop(thread::spawn(move || launch(job))),
                None => drop(SetTimer(h, TIMER_POLL, 300, None)),
            }
        }

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_play_link() {
        let j = parse_link("okakiiclient://join?place=1818&ticket=abc%7C123&year=2021/").unwrap();
        assert_eq!((j.place.as_str(), j.ticket.as_str(), j.year), ("1818", "abc|123", "2021"));
        assert_eq!(parse_link("OKAKIICLIENT://x?place=1&ticket=t&year=1999").unwrap().year, "2017");
    }

    #[test]
    fn rejects_injection() {
        assert!(parse_link("okakiiclient://join?place=1&ticket=a%22%20-evil").is_none());
        assert!(parse_link("okakiiclient://join?place=1&ticket=a%26x%3D1").is_none());
        assert!(parse_link("okakiiclient://join?place=1%20x&ticket=t").is_none());
        assert!(parse_link("https://join?place=1&ticket=t").is_none());
        assert!(parse_link("okakiiclient://join").is_none());
    }

    #[test]
    fn percent_roundtrip() {
        assert_eq!(percent_decode("a%2Fb%zz%4"), "a/b%zz%4");
        assert_eq!(percent_encode("1.0 +x"), "1.0%20%2Bx");
    }
}
