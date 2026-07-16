export type Role = "system" | "user" | "assistant" | "tool";

export interface ChatMessage {
  id: string;
  role: Role;
  content: string;
  status?: "local" | "loading" | "success" | "error";
  attachments?: ConversationAttachment[];
  artifacts?: ConversationArtifactRecord[];
  diagnostics?: ConversationDiagnosticRecord[];
  audioUrl?: string;
  audioMediaType?: string | null;
  transcript?: string | null;
}

export interface Conversation {
  id: string;
  backendId?: string;
  title: string;
  updatedAt: number;
  messages: ChatMessage[];
  loaded?: boolean;
  loading?: boolean;
}

export interface ConversationCreateRequest {
  title?: string;
  model?: string;
  metadata?: Record<string, unknown>;
}

export interface ConversationCreateResponse {
  status: string;
  conversation: ConversationRecord;
}

export interface ConversationDeleteResponse {
  status: string;
  conversation: ConversationRecord;
}

export interface ConversationListResponse {
  status: string;
  checked_at: string;
  conversations: ConversationRecord[];
}

export interface ConversationDetailResponse {
  status: string;
  conversation: ConversationRecord;
  messages: ConversationMessageRecord[];
  artifacts: ConversationArtifactRecord[];
  diagnostics: ConversationDiagnosticRecord[];
}

export interface ConversationAppendMessageRequest {
  role?: string;
  content?: string;
  modality?: string;
  status?: string;
  model?: string;
  attachments?: ConversationAttachment[];
  artifact_ids?: string[];
  metadata?: Record<string, unknown>;
}

export interface ConversationAppendMessageResponse {
  status: string;
  conversation: ConversationRecord;
  message: ConversationMessageRecord;
}

export interface ConversationTurnRequest {
  content?: string;
  modality?: string;
  model?: string;
  attachments?: ConversationAttachment[];
  tool_mode?: string;
  max_tool_rounds?: number;
  instructions?: string;
  max_tokens?: number;
  temperature?: number;
  top_p?: number;
  history_limit?: number;
  metadata?: Record<string, unknown>;
  confirm?: boolean;
  speak?: boolean;
  voice?: string;
  tts_model?: string;
  response_format?: string;
  speed?: number;
  language?: string;
}

export interface ConversationTurnResponse {
  status: string;
  conversation: ConversationRecord;
  messages: ConversationMessageRecord[];
  user_message: ConversationMessageRecord;
  tool_message?: ConversationMessageRecord | null;
  assistant_message?: ConversationMessageRecord | null;
  diagnostics: ConversationDiagnosticRecord[];
  artifacts: ConversationArtifactRecord[];
  speech_artifact?: ConversationArtifactRecord | null;
  speech_media_type?: string | null;
  speech_bytes?: number | null;
}

export interface ConversationVoiceTurnResponse {
  status: string;
  conversation: ConversationRecord;
  transcript?: string | null;
  input_artifact?: ConversationArtifactRecord | null;
  user_message?: ConversationMessageRecord | null;
  tool_message?: ConversationMessageRecord | null;
  assistant_message?: ConversationMessageRecord | null;
  speech_artifact?: ConversationArtifactRecord | null;
  diagnostics: ConversationDiagnosticRecord[];
  turn?: ConversationTurnResponse | null;
  speech_media_type?: string | null;
  speech_bytes?: number | null;
}

export interface ConversationRecord {
  id: string;
  title: string;
  status: string;
  model?: string | null;
  created_at: string;
  updated_at: string;
  last_message_at?: string | null;
  message_count: number;
  artifact_count: number;
  diagnostic_count: number;
  metadata?: Record<string, unknown> | null;
}

export interface ConversationMessageRecord {
  id: string;
  conversation_id: string;
  role: string;
  content: string;
  modality: string;
  status: string;
  model?: string | null;
  created_at: string;
  attachments: ConversationAttachment[];
  tool_calls: ConversationToolCall[];
  artifact_ids: string[];
  metadata?: Record<string, unknown> | null;
}

export interface ConversationAttachment {
  id?: string | null;
  type?: string | null;
  name?: string | null;
  media_type?: string | null;
  path?: string | null;
  bytes?: number | null;
  metadata?: Record<string, unknown> | null;
  data_uri?: string | null;
  base64?: string | null;
  text?: string | null;
  content?: string | null;
}

export interface ConversationToolCall {
  tool?: string | null;
  status?: string | null;
  artifact_id?: string | null;
  result?: string | null;
  result_json?: Record<string, unknown> | null;
  diagnostic?: RuntimeDiagnostic | null;
}

export interface ConversationArtifactRecord {
  id: string;
  conversation_id: string;
  type: string;
  path?: string | null;
  media_type?: string | null;
  source?: string | null;
  status: string;
  bytes?: number | null;
  created_at: string;
  metadata?: Record<string, unknown> | null;
}

export interface ConversationDiagnosticRecord {
  id: string;
  conversation_id: string;
  status: string;
  code: string;
  message: string;
  model?: string | null;
  backend?: string | null;
  created_at: string;
  actions: string[];
  metadata?: Record<string, unknown> | null;
}

export interface OpenAiModelListResponse {
  object: "list";
  data: OpenAiModel[];
}

export interface OpenAiModel {
  id: string;
  object: "model";
  created: number;
  owned_by: string;
  family?: string;
  format?: string;
  quantization?: string;
  capabilities?: string[];
}

export interface OpenAiChatCompletionResponse {
  id: string;
  object: "chat.completion";
  created: number;
  model: string;
  choices: Array<{
    index: number;
    message: {
      role: "assistant";
      content: string;
    };
    finish_reason: string;
  }>;
  usage?: {
    prompt_tokens: number;
    completion_tokens: number;
    total_tokens: number;
  };
}

export interface OpenAiErrorResponse {
  error: {
    message: string;
    type: string;
    code?: string;
    diagnostics?: string[];
  };
}

export interface VersionResponse {
  version: string;
}

export interface RuntimeStatusResponse {
  status: string;
  checked_at: string;
  version: string;
  system: SystemSnapshot;
  paths: PathConfiguration;
  configuration: ConfigurationState;
  directories: DirectoryState[];
  database: LocalDatabaseState;
  api_keys: ApiKeyStoreState;
  disk: DiskState;
  proxy: ProxyState;
  port: PortState;
  acceleration: AccelerationPlan;
  native_bundle: NativeBundleStatus;
  managed_providers: ManagedProviderStatus;
  managed_models: ModelReadinessStatus[];
  session: SessionSnapshot;
  runtime: RuntimeDiagnostic;
  diagnostics: DiagnosticItem[];
}

export interface ManagedProviderStatus {
  status: string;
  dynamic_loading_supported: boolean;
  search_directories: string[];
  loaded: ManagedProviderInfo[];
  diagnostics: ManagedProviderLoadDiagnostic[];
}

export interface ManagedProviderInfo {
  id: string;
  assembly: string;
  version?: string | null;
  path: string;
}

export interface ManagedProviderLoadDiagnostic {
  code: string;
  message: string;
  path?: string | null;
}

export interface ModelReadinessStatus {
  model_id: string;
  provider_id?: string | null;
  architecture: string;
  quantization: string;
  quantization_layout?: string | null;
  status: string;
  provider_discovered: boolean;
  metadata_valid: boolean;
  assets_complete: boolean;
  forward_verified: boolean;
  session_loaded: boolean;
  context_size?: number | null;
  tensor_file_count?: number | null;
  tensor_count?: number | null;
  resident_bytes?: number | null;
  kv_bytes?: number | null;
  scratch_bytes?: number | null;
  expert_cache_bytes?: number | null;
  required_bytes?: number | null;
  available_bytes?: number | null;
  diagnostics: ModelReadinessDiagnostic[];
}

export interface ModelReadinessDiagnostic {
  code: string;
  message: string;
  actions: string[];
}

export interface SessionSnapshot {
  loaded: boolean;
  model_id?: string | null;
  model_path?: string | null;
  mode?: string | null;
  loaded_at?: string | null;
  request_count: number;
  prompt_tokens: number;
  completion_tokens: number;
  diagnostics: string[];
  provider_id?: string | null;
  architecture?: string | null;
  quantization?: string | null;
  busy: boolean;
  context_size?: number | null;
  resident_bytes?: number | null;
  kv_bytes?: number | null;
  scratch_bytes?: number | null;
  expert_cache_bytes?: number | null;
  expert_cache_hits?: number | null;
  expert_cache_misses?: number | null;
  expert_cache_evictions?: number | null;
  expert_disk_reads?: number | null;
  expert_disk_bytes?: number | null;
  load_elapsed_milliseconds?: number | null;
  last_first_token_milliseconds?: number | null;
  last_generation_milliseconds?: number | null;
  last_output_tokens_per_second?: number | null;
  last_decode_tokens_per_second?: number | null;
  last_error?: SessionErrorSnapshot | null;
}

export interface SessionErrorSnapshot {
  code: string;
  message: string;
  occurred_at: string;
}

export interface SystemSnapshot {
  os_description: string;
  process_architecture: string;
  framework_description: string;
  processor_count: number;
  cpu_name?: string | null;
  total_memory_bytes?: number | null;
}

export interface PathConfiguration {
  data_directory: string;
  models_directory: string;
  runtime_directory: string;
  logs_directory: string;
  database_path: string;
}

export interface ConfigurationState {
  status: string;
  path: string;
  message: string;
  recovered_path?: string | null;
  configuration: LocalConfiguration;
}

export interface LocalConfiguration {
  schema_version: number;
  server: {
    urls: string;
  };
  paths: PathConfiguration;
  runtime: {
    default_backend: string;
    accelerator?: RuntimeAcceleratorConfiguration | null;
  };
}

export interface RuntimeAcceleratorConfiguration {
  preference: string;
  device_selection_key?: string | null;
  gpu_layers?: number | null;
  openvino_device?: string | null;
  allow_npu: boolean;
  npu_prefill_chunk?: number | null;
}

export interface DirectoryState {
  name: string;
  path: string;
  status: string;
  message: string;
}

export interface DiskState {
  path: string;
  drive: string;
  available_bytes?: number | null;
  total_bytes?: number | null;
  status: string;
  message: string;
}

export interface LocalDatabaseState {
  status: string;
  path: string;
  schema_version: number;
  message: string;
}

export interface ApiKeyStoreState {
  status: string;
  active_key_count: number;
  message: string;
  keys: ApiKeyRecord[];
}

export interface ApiKeyRecord {
  id: string;
  name: string;
  prefix: string;
  created_at: string;
  last_used_at?: string | null;
}

export interface ProxyState {
  status: string;
  http_proxy?: string | null;
  https_proxy?: string | null;
  no_proxy?: string | null;
  message: string;
}

export interface PortState {
  url: string;
  host: string;
  port?: number | null;
  status: string;
  message: string;
}

export interface NativeBundleStatus {
  status: string;
  checked_at: string;
  rid: string;
  bundle_id: string;
  version: string;
  manifest_path: string;
  source_runtime_root: string;
  runtime_root: string;
  components: NativeComponentStatus[];
  message: string;
}

export interface NativeComponentStatus {
  id: string;
  display_name: string;
  status: string;
  backend: string;
  runtime_path: string;
  capabilities: string[];
  message: string;
}

export interface NativeBundlePrepareResult {
  status: string;
  prepared_at: string;
  rid: string;
  bundle_id: string;
  version: string;
  manifest_path: string;
  source_runtime_root: string;
  runtime_root: string;
  files: NativeBundleFilePrepareResult[];
  message: string;
}

export interface NativeBundleFilePrepareResult {
  source_path: string;
  destination_path: string;
  status: string;
  size_bytes?: number | null;
  sha256?: string | null;
  message: string;
}

export interface RuntimeDiagnostic {
  status: string;
  code: string;
  message: string;
  model?: string | null;
  actions: string[];
}

export interface AccelerationPlan {
  status: string;
  preferred_backend: string;
  effective_backend: string;
  configured_gpu_layers: number;
  effective_gpu_layers: number;
  recommended_gpu_layers: number;
  selected_accelerator_key?: string | null;
  selected_accelerator?: AcceleratorDevice | null;
  configured_accelerator_key?: string | null;
  openvino_device?: string | null;
  allow_npu: boolean;
  npu_prefill_chunk?: number | null;
  fallback_reason?: string | null;
  devices: AcceleratorDevice[];
  backends: AccelerationBackendStatus[];
  actions: string[];
}

export interface AcceleratorDevice {
  device_index: number;
  kind: string;
  name: string;
  memory_bytes?: number | null;
  selection_key: string;
  backend: string;
  integrated: boolean;
  device_id?: string | null;
}

export interface AccelerationBackendStatus {
  id: string;
  display_name: string;
  library_name: string;
  status: string;
  path?: string | null;
  message: string;
  actions: string[];
}

export interface DiagnosticItem {
  name: string;
  status: string;
  severity: string;
  message: string;
  value?: string | null;
  actions: string[];
}

export interface InstalledModelsResponse {
  models_directory: string;
  packages: InstalledModelPackage[];
  visible_models: VisibleModel[];
}

export interface InstalledModelPackage {
  id: string;
  model_key: string;
  display_name: string;
  segment: string;
  directory: string;
  primary_path: string;
  status: string;
  license?: string | null;
  license_notice: string;
  installed_at_utc: string;
  updated_at_utc: string;
  assets: InstalledModelAsset[];
}

export interface InstalledModelAsset {
  path: string;
  source_repository_id: string;
  source_relative_path: string;
  expected_sha256?: string | null;
  actual_sha256?: string | null;
  sha256_verified: boolean;
  size_bytes: number;
}

export interface VisibleModel {
  id: string;
  name: string;
  package_id?: string | null;
  relative_path: string;
  size_bytes: number;
  format: string;
  family: string;
  quantization_level: string;
  capabilities: string[];
  verified: boolean;
}

export interface ModelCatalogResponse {
  hardware: {
    os_description: string;
    process_architecture: string;
    processor_count: number;
    total_memory_bytes?: number | null;
    tier: string;
    recommendations: string[];
    acceleration: AccelerationPlan;
  };
  packages: ModelCatalogPackage[];
}

export interface ModelCatalogPackage {
  id: string;
  model_key: string;
  display_name: string;
  description: string;
  segment: string;
  task: string;
  runtime: string;
  family: string;
  format: string;
  quantization?: string | null;
  license?: string | null;
  size_bytes?: number | null;
  parameter_count?: number | null;
  primary_file_name?: string | null;
  recommended: boolean;
  optional: boolean;
  research: boolean;
  installed: boolean;
  install_status: string;
  minimum_memory_bytes?: number | null;
  hardware_tier: string;
  license_notice: string;
  tags: string[];
  assets: ModelCatalogAsset[];
  bundle_assets: ModelCatalogBundleAsset[];
}

export interface ModelCatalogAsset {
  repository_id: string;
  relative_path: string;
  target_relative_path: string;
  expected_sha256?: string | null;
  source_kind: string;
}

export interface ModelCatalogBundleAsset {
  asset_key: string;
  role: string;
  is_required: boolean;
  relative_path: string;
  file_name: string;
  format?: string | null;
  quantization?: string | null;
  license?: string | null;
  size_bytes?: number | null;
  expected_sha256?: string | null;
  description: string;
}

export interface MultimodalRuntimeStatus {
  status: string;
  checked_at: string;
  backends: MultimodalBackendStatus[];
  actions: string[];
}

export interface MultimodalBackendStatus {
  id: string;
  display_name: string;
  capability: string;
  status: string;
  native_component_id: string;
  native_status?: string | null;
  native_message?: string | null;
  model_requirement: string;
  visible_model_ids: string[];
  message: string;
  actions: string[];
}

export interface AgentRuntimeStatus {
  status: string;
  checked_at: string;
  chat_client: AgentChatClientStatus;
  agent_framework: AgentFrameworkStatus;
  orchestration: AgentOrchestrationStatus;
  tools: AgentToolStatus[];
  notes: string[];
}

export interface AgentChatClientStatus {
  status: string;
  provider: string;
  default_model?: string | null;
  message: string;
}

export interface AgentFrameworkStatus {
  status: string;
  runtime: string;
  message: string;
  actions: string[];
}

export interface AgentOrchestrationStatus {
  status: string;
  agent_type: string;
  endpoint: string;
  message: string;
}

export interface AgentToolStatus {
  name: string;
  display_name: string;
  status: string;
  backend: string;
  model?: string | null;
  route?: string | null;
  input_schema: string;
  side_effect: string;
  callable: boolean;
  requires_confirmation: boolean;
  invocation_modes: string[];
  message: string;
  actions: string[];
}

export interface AgentToolMapResponse {
  status: string;
  checked_at: string;
  tools: AgentToolDescriptor[];
}

export interface AgentToolDescriptor {
  name: string;
  display_name: string;
  status: string;
  backend: string;
  model?: string | null;
  route?: string | null;
  input_schema: string;
  side_effect: string;
  callable: boolean;
  requires_confirmation: boolean;
  invocation_modes: string[];
  message: string;
  actions: string[];
}

export interface AgentFrameworkToolBindingResponse {
  status: string;
  checked_at: string;
  tool_type: string;
  safe_tools: AgentFrameworkToolBinding[];
  declaration_tools: AgentFrameworkToolBinding[];
  notes: string[];
}

export interface AgentFrameworkToolBinding {
  name: string;
  description: string;
  implementation: string;
  status: string;
  route?: string | null;
  input_schema: string;
  side_effect: string;
  callable: boolean;
  requires_confirmation: boolean;
  invocation_modes: string[];
}

export interface AgentEventLogRecentResponse {
  status: string;
  path: string;
  count: number;
  events: AgentEventLogEntry[];
}

export interface AgentEventLogEntry {
  id: string;
  recorded_at: string;
  event: string;
  status: string;
  mode?: string | null;
  tool?: string | null;
  runtime?: string | null;
  model?: string | null;
  elapsed_ms: number;
  blocked: boolean;
  side_effect?: string | null;
  requires_confirmation?: boolean | null;
  tool_rounds?: number | null;
  step_count?: number | null;
  diagnostics: string[];
  actions: string[];
}

export interface AgentTelemetryStatus {
  status: string;
  checked_at: string;
  source_name: string;
  instrumentation: string;
  exporter: AgentTelemetryExporterStatus;
  local_event_log?: string | null;
  spans: AgentTelemetrySpanDescriptor[];
  attributes: AgentTelemetryAttributeDescriptor[];
  notes: string[];
}

export interface AgentTelemetryExporterStatus {
  status: string;
  exporter: string;
  endpoint?: string | null;
  headers_configured: boolean;
  message: string;
  actions: string[];
}

export interface AgentTelemetrySpanDescriptor {
  name: string;
  event: string;
  description: string;
  attributes: string[];
}

export interface AgentTelemetryAttributeDescriptor {
  name: string;
  type: string;
  cardinality: string;
  description: string;
}

export interface AgentToolInvokeRequest {
  tool?: string | null;
  arguments?: Record<string, unknown> | null;
  mode?: string | null;
  confirm?: boolean | null;
}

export interface AgentToolInvokeResponse {
  status: string;
  tool: string;
  tool_type: string;
  implementation: string;
  input_schema: string;
  elapsed_ms: number;
  result?: unknown;
  diagnostics: string[];
  audit: AgentToolInvokeAudit;
}

export interface AgentToolInvokeAudit {
  invoked_at: string;
  mode: string;
  side_effect: string;
  requires_confirmation: boolean;
  actions: string[];
}

export type LogLevel = "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical";

export interface LogStreamEntry {
  seq: number;
  timestamp: string;
  level: string;
  category: string;
  event_id: number;
  event_name?: string | null;
  message: string;
  exception?: string | null;
}

export interface LogRecentResponse {
  status: string;
  count: number;
  capacity: number;
  dropped: number;
  entries: LogStreamEntry[];
}

export interface LogClearResponse {
  status: string;
  cleared: number;
}
