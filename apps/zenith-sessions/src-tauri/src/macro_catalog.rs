use std::{collections::HashSet, time::Duration};

use serde::{Deserialize, Deserializer, Serialize};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::{ClientOptions, NamedPipeClient},
    sync::Mutex,
    time::{Instant, sleep, timeout, timeout_at},
};
use uuid::Uuid;
use windows::Win32::{Foundation::ERROR_PIPE_BUSY, Storage::FileSystem::SECURITY_ANONYMOUS};

mod peer;

const PRODUCT: &str = "Zenith.Macros.Personal";
const PIPE: &str = r"\\.\pipe\Zenith.Macros.Personal.Catalog.v1";
const OPERATION: &str = "Catalog";
const VERSION: u8 = 1;
const MAX_FRAME: usize = 256 * 1024;
const MAX_ITEMS: usize = 10_000;
const PAGE_SIZE: usize = 25;
const INVALID_CURSOR: &str = "Refresh the saved macro catalog before changing pages.";

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub enum Status {
    Ready,
    Starting,
    Changed,
    Unavailable,
    Incompatible,
    Disconnected,
    Busy,
}

impl Status {
    // Compare against the v1 contract. The workspace displays its own text for these states.
    fn explanation(self) -> &'static str {
        match self {
            Self::Ready => {
                "Saved macro metadata is available. This link cannot execute macros or verify an account."
            }
            Self::Starting => "The Personal host is loading its saved macro metadata.",
            Self::Changed => "The saved macro catalog changed. Refresh from the first page.",
            Self::Unavailable => {
                "The saved macro library could not be fully confirmed. Reload it in the Personal host."
            }
            Self::Incompatible => "The Personal host catalog protocol or metadata is incompatible.",
            Self::Disconnected => {
                "The Personal macro host is not connected. Open it explicitly, then refresh."
            }
            Self::Busy => {
                "The Personal host catalog is busy. Refresh again after its current request finishes."
            }
        }
    }
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Cursor {
    offset: usize,
    host_session_id: Uuid,
    catalog_revision: String,
    total: usize,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Choice {
    page: Cursor,
    macro_id: Uuid,
    saved_revision: Uuid,
}

pub struct VerifiedMacro {
    pub(crate) id: Uuid,
    pub(crate) name: String,
    pub(crate) revision: Uuid,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Query {
    offset: usize,
    limit: usize,
    host_session_id: Option<Uuid>,
    catalog_revision: Option<i64>,
    #[serde(skip)]
    expected_total: Option<usize>,
}

impl Query {
    fn from_cursor(cursor: Option<Cursor>) -> Result<Self, &'static str> {
        let mut query = Self {
            offset: 0,
            limit: PAGE_SIZE,
            host_session_id: None,
            catalog_revision: None,
            expected_total: None,
        };
        if let Some(cursor) = cursor {
            let revision: i64 = cursor
                .catalog_revision
                .parse()
                .map_err(|_| INVALID_CURSOR)?;
            if cursor.host_session_id.is_nil()
                || revision <= 0
                || cursor.total > MAX_ITEMS
                || cursor.offset >= cursor.total
                || !cursor.offset.is_multiple_of(PAGE_SIZE)
            {
                return Err(INVALID_CURSOR);
            }
            query.offset = cursor.offset;
            query.host_session_id = Some(cursor.host_session_id);
            query.catalog_revision = Some(revision);
            query.expected_total = Some(cursor.total);
        }
        Ok(query)
    }
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Request<'a> {
    product: &'static str,
    version: u8,
    request_id: Uuid,
    operation: &'static str,
    query: &'a Query,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct Response {
    product: String,
    version: u8,
    request_id: Uuid,
    operation: String,
    page: WirePage,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct WirePage {
    state: Status,
    host_session_id: Uuid,
    catalog_revision: i64,
    host_version: String,
    items: Vec<Descriptor>,
    total: usize,
    #[serde(deserialize_with = "required_option")]
    next_offset: Option<usize>,
    explanation: String,
}

fn required_option<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    Option::deserialize(deserializer)
}

#[derive(Deserialize, Serialize)]
#[serde(
    rename_all(serialize = "camelCase", deserialize = "PascalCase"),
    deny_unknown_fields
)]
struct Descriptor {
    id: Uuid,
    saved_revision: Uuid,
    name: String,
    hotkey: String,
    top_level_actions: u32,
    enabled: bool,
    target_mode: TargetMode,
}

#[derive(Deserialize, Serialize)]
enum TargetMode {
    Legacy,
    Screen,
    ActiveExternalWindow,
    Application,
    SpecificWindow,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogView {
    state: Status,
    host_version: String,
    items: Vec<Descriptor>,
    total: usize,
    offset: usize,
    previous: Option<Cursor>,
    next: Option<Cursor>,
    selection_page: Option<Cursor>,
}

impl CatalogView {
    fn unavailable(state: Status) -> Self {
        Self {
            state,
            host_version: String::new(),
            items: Vec::new(),
            total: 0,
            offset: 0,
            previous: None,
            next: None,
            selection_page: None,
        }
    }
}

fn valid_text(text: &str, limit: usize, allow_empty: bool) -> bool {
    text.encode_utf16().count() <= limit
        && (allow_empty || !text.trim().is_empty())
        && !text.chars().any(char::is_control)
}

fn decode(bytes: &[u8], request_id: Uuid, query: &Query) -> Result<CatalogView, Status> {
    let response: Response = serde_json::from_slice(bytes).map_err(|_| Status::Incompatible)?;
    if response.product != PRODUCT
        || response.version != VERSION
        || response.request_id != request_id
        || response.operation != OPERATION
    {
        return Err(Status::Incompatible);
    }
    let WirePage {
        state,
        host_session_id,
        catalog_revision,
        host_version,
        items,
        total,
        next_offset,
        explanation,
    } = response.page;
    let known = !host_session_id.is_nil() && catalog_revision > 0;
    let unknown = host_session_id.is_nil() && catalog_revision == 0;
    if (!known && !unknown)
        || !valid_text(&host_version, 64, unknown)
        || explanation != state.explanation()
    {
        return Err(Status::Incompatible);
    }
    if state != Status::Ready {
        if !items.is_empty() || total != 0 || next_offset.is_some() {
            return Err(Status::Incompatible);
        }
        return Ok(CatalogView::unavailable(state));
    }
    if !known
        || total > MAX_ITEMS
        || query.offset > total
        || items.len() != query.limit.min(total - query.offset)
        || query
            .host_session_id
            .is_some_and(|expected| expected != host_session_id)
        || query
            .catalog_revision
            .is_some_and(|expected| expected != catalog_revision)
        || query
            .expected_total
            .is_some_and(|expected| expected != total)
    {
        return Err(Status::Incompatible);
    }
    let end = query.offset + items.len();
    if next_offset != (end < total).then_some(end) {
        return Err(Status::Incompatible);
    }
    let mut ids = HashSet::with_capacity(items.len());
    if items.iter().any(|item| {
        item.id.is_nil()
            || item.saved_revision.is_nil()
            || !ids.insert(item.id)
            || !valid_text(&item.name, 200, false)
            || !valid_text(&item.hotkey, 80, true)
            || item.top_level_actions > 100_000
    }) {
        return Err(Status::Incompatible);
    }
    let cursor = |offset| Cursor {
        offset,
        total,
        host_session_id,
        catalog_revision: catalog_revision.to_string(),
    };
    Ok(CatalogView {
        state,
        host_version,
        items,
        total,
        offset: query.offset,
        previous: query.offset.checked_sub(query.limit).map(cursor),
        next: next_offset.map(cursor),
        selection_page: (end > query.offset).then(|| cursor(query.offset)),
    })
}

#[derive(Default)]
pub struct MacroCatalog {
    active: Mutex<()>,
}

impl MacroCatalog {
    pub async fn page(&self, cursor: Option<Cursor>) -> Result<CatalogView, &'static str> {
        let query = Query::from_cursor(cursor)?;
        Ok(self.read(PIPE, &query).await)
    }

    pub async fn verify_choice(&self, choice: Choice) -> Result<VerifiedMacro, &'static str> {
        self.verify_choice_at(PIPE, choice).await
    }

    async fn verify_choice_at(
        &self,
        path: &str,
        choice: Choice,
    ) -> Result<VerifiedMacro, &'static str> {
        let query = Query::from_cursor(Some(choice.page))?;
        let page = self.read(path, &query).await;
        if page.state != Status::Ready {
            return Err(page.state.explanation());
        }
        let item = page
            .items
            .into_iter()
            .find(|item| item.id == choice.macro_id && item.saved_revision == choice.saved_revision)
            .ok_or("The selected macro changed. Refresh the catalog and select it again.")?;
        Ok(VerifiedMacro {
            id: item.id,
            name: item.name,
            revision: item.saved_revision,
        })
    }

    async fn read(&self, path: &str, query: &Query) -> CatalogView {
        let Ok(_active) = self.active.try_lock() else {
            return CatalogView::unavailable(Status::Busy);
        };
        let deadline = Instant::now() + Duration::from_secs(3);
        match timeout_at(deadline, exchange(path, query)).await {
            Ok(Ok(page)) if Instant::now() < deadline => page,
            Ok(Err(status)) if Instant::now() < deadline => CatalogView::unavailable(status),
            _ => CatalogView::unavailable(Status::Busy),
        }
    }
}

async fn connect(path: &str) -> Result<NamedPipeClient, Status> {
    timeout(Duration::from_secs(1), async {
        loop {
            match ClientOptions::new()
                .security_qos_flags(SECURITY_ANONYMOUS.0)
                .open(path)
            {
                Ok(pipe) => return Ok(pipe),
                Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY.0 as i32) => {
                    sleep(Duration::from_millis(40)).await
                }
                Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
                    return Err(Status::Unavailable);
                }
                Err(_) => return Err(Status::Disconnected),
            }
        }
    })
    .await
    .unwrap_or(Err(Status::Busy))
}

async fn exchange(path: &str, query: &Query) -> Result<CatalogView, Status> {
    let mut pipe = connect(path).await?;
    if !peer::owned_by_current_user(&pipe) {
        return Err(Status::Unavailable);
    }
    let request_id = Uuid::new_v4();
    let body = serde_json::to_vec(&Request {
        product: PRODUCT,
        version: VERSION,
        request_id,
        operation: OPERATION,
        query,
    })
    .map_err(|_| Status::Incompatible)?;
    pipe.write_u32_le(body.len() as u32)
        .await
        .map_err(|_| Status::Disconnected)?;
    pipe.write_all(&body)
        .await
        .map_err(|_| Status::Disconnected)?;
    let size = pipe.read_u32_le().await.map_err(|_| Status::Disconnected)? as usize;
    if size == 0 || size > MAX_FRAME {
        return Err(Status::Incompatible);
    }
    let mut response = vec![0; size];
    pipe.read_exact(&mut response)
        .await
        .map_err(|_| Status::Disconnected)?;
    decode(&response, request_id, query)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};

    fn fixture_name() -> String {
        format!(
            "Zenith.Macros.Personal.Catalog.v1.fixture-{}",
            Uuid::new_v4()
        )
    }

    fn ready(id: Uuid) -> Value {
        let items: Vec<_> = (0..PAGE_SIZE).map(|index| json!({
            "Id": Uuid::from_u128(index as u128 + 1), "SavedRevision": Uuid::from_u128(50),
            "Name": "Fixture macro", "Hotkey": "Ctrl+9", "TopLevelActions": 3, "Enabled": true, "TargetMode": "Screen"
        })).collect();
        json!({ "Product": PRODUCT, "Version": VERSION, "RequestId": id, "Operation": OPERATION,
            "Page": { "State": "Ready", "HostSessionId": Uuid::from_u128(40), "CatalogRevision": 9007199254740993_i64,
                "HostVersion": "fixture", "Items": items, "Total": 26, "NextOffset": 25, "Explanation": Status::Ready.explanation() } })
    }

    #[test]
    fn catalog_pages_bind_exact_revision_total_and_request_id() {
        let id = Uuid::new_v4();
        let mut body = ready(id);
        let first = Query::from_cursor(None).unwrap();
        let page = decode(&serde_json::to_vec(&body).unwrap(), id, &first).unwrap();
        let next = page.next.unwrap();
        assert_eq!(next.catalog_revision, "9007199254740993");
        assert!(page.previous.is_none());
        let next = Query::from_cursor(Some(next)).unwrap();
        let last = body["Page"]["Items"][24].clone();
        body["Page"]["Items"] = json!([last]);
        body["Page"]["NextOffset"] = Value::Null;
        let second = decode(&serde_json::to_vec(&body).unwrap(), id, &next).unwrap();
        assert_eq!(second.previous.unwrap().offset, 0);
        assert!(second.next.is_none());
        assert!(decode(&serde_json::to_vec(&body).unwrap(), Uuid::new_v4(), &next).is_err());
        body["Page"]["CatalogRevision"] = json!(9007199254740992_i64);
        assert!(decode(&serde_json::to_vec(&body).unwrap(), id, &next).is_err());
        body["Page"]["CatalogRevision"] = json!(9007199254740993_i64);
        body["Page"]["Total"] = json!(25);
        body["Page"]["Items"] = json!([]);
        assert!(decode(&serde_json::to_vec(&body).unwrap(), id, &next).is_err());
    }

    #[test]
    fn catalog_rejects_missing_duplicate_and_out_of_contract_metadata() {
        let id = Uuid::new_v4();
        let first = Query::from_cursor(None).unwrap();
        let mut body = ready(id);
        body["Page"].as_object_mut().unwrap().remove("NextOffset");
        assert!(decode(&serde_json::to_vec(&body).unwrap(), id, &first).is_err());
        let mut body = ready(id);
        body["Page"]["Items"][1]["Id"] = body["Page"]["Items"][0]["Id"].clone();
        assert!(decode(&serde_json::to_vec(&body).unwrap(), id, &first).is_err());
        let mut body = ready(id);
        body["Page"]["Items"][0]["DocumentBody"] = json!("no contents may cross");
        assert!(decode(&serde_json::to_vec(&body).unwrap(), id, &first).is_err());
        let body = ready(id)
            .to_string()
            .replace("\"Version\":1", "\"Version\":1,\"Version\":1");
        assert!(decode(body.as_bytes(), id, &first).is_err());
    }

    #[test]
    fn native_pipe_rejects_oversize_frames_and_bounds_a_stalled_reply() {
        use tokio::net::windows::named_pipe::ServerOptions;
        tauri::async_runtime::block_on(async {
            let name = fixture_name();
            let path = format!(r"\\.\pipe\{name}");
            let mut server = ServerOptions::new()
                .first_pipe_instance(true)
                .reject_remote_clients(true)
                .create(&path)
                .unwrap();
            let client = MacroCatalog::default();
            let query = Query::from_cursor(None).unwrap();
            let worker = tokio::spawn(async move {
                server.connect().await.unwrap();
                let size = server.read_u32_le().await.unwrap();
                assert!(size < 1024);
                server
                    .read_exact(&mut vec![0; size as usize])
                    .await
                    .unwrap();
                server.write_u32_le(MAX_FRAME as u32 + 1).await.unwrap();
                let mut end = [0];
                let _ = server.read(&mut end).await;
            });
            assert_eq!(client.read(&path, &query).await.state, Status::Incompatible);
            worker.await.unwrap();

            let name = fixture_name();
            let path = format!(r"\\.\pipe\{name}");
            let mut server = ServerOptions::new()
                .first_pipe_instance(true)
                .reject_remote_clients(true)
                .create(&path)
                .unwrap();
            let stalled = tokio::spawn(async move {
                server.connect().await.unwrap();
                let size = server.read_u32_le().await.unwrap();
                assert!(size < 1024);
                server
                    .read_exact(&mut vec![0; size as usize])
                    .await
                    .unwrap();
                let mut end = [0];
                let _ = server.read(&mut end).await;
            });
            let start = std::time::Instant::now();
            assert_eq!(client.read(&path, &query).await.state, Status::Busy);
            assert!(start.elapsed() >= Duration::from_secs(3));
            stalled.await.unwrap();
            assert_eq!(client.read(&path, &query).await.state, Status::Disconnected);
        });
    }

    #[test]
    #[ignore = "Run against the isolated .NET Personal catalog fixture worker"]
    fn rust_reads_the_existing_dotnet_catalog_protocol() {
        use std::{
            io::{BufRead, BufReader, Write},
            os::windows::process::CommandExt,
            process::{Child, Command, Stdio},
        };
        struct Fixture(Child);
        impl Drop for Fixture {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
        let worker = std::env::var_os("ZENITH_MACRO_FIXTURE").unwrap();
        let name = fixture_name();
        let path = format!(r"\\.\pipe\{name}");
        let mut fixture = Fixture(
            Command::new("dotnet")
                .arg(worker)
                .args(["--pipe", &name])
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::null())
                .creation_flags(0x0800_0000)
                .spawn()
                .unwrap(),
        );
        let mut line = String::new();
        BufReader::new(fixture.0.stdout.take().unwrap())
            .read_line(&mut line)
            .unwrap();
        assert_eq!(line.trim(), "READY NET8");
        tauri::async_runtime::block_on(async {
            let client = MacroCatalog::default();
            let page = client.read(&path, &Query::from_cursor(None).unwrap()).await;
            assert_eq!(page.state, Status::Ready);
            assert_eq!(page.host_version, "2.5.12-fixture");
            assert_eq!(page.items[0].name, "Cross-runtime fixture");
            let reviewed = Choice {
                page: page.selection_page.clone().unwrap(),
                macro_id: page.items[0].id,
                saved_revision: page.items[0].saved_revision,
            };
            let verified = client.verify_choice_at(&path, reviewed).await.unwrap();
            assert_eq!(verified.id, page.items[0].id);
            assert_eq!(verified.revision, page.items[0].saved_revision);
            let changed = Choice {
                page: page.selection_page.unwrap(),
                macro_id: verified.id,
                saved_revision: Uuid::new_v4(),
            };
            assert!(client.verify_choice_at(&path, changed).await.is_err());
            let stale = Cursor {
                offset: 0,
                host_session_id: Uuid::new_v4(),
                catalog_revision: "1".into(),
                total: 1,
            };
            assert_eq!(
                client
                    .read(&path, &Query::from_cursor(Some(stale)).unwrap())
                    .await
                    .state,
                Status::Changed
            );
        });
        writeln!(fixture.0.stdin.take().unwrap(), "EXIT").unwrap();
        assert!(fixture.0.wait().unwrap().success());
    }
}
