export type Role = "system" | "user" | "assistant";

export interface ChatMessage {
  id: string;
  role: Role;
  content: string;
  status?: "local" | "loading" | "success" | "error";
}

export interface Conversation {
  id: string;
  title: string;
  updatedAt: number;
  messages: ChatMessage[];
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
  paths: PathConfiguration;
  configuration: ConfigurationState;
  directories: DirectoryState[];
  disk: DiskState;
  proxy: ProxyState;
  port: PortState;
  native_bundle: NativeBundleStatus;
  runtime: RuntimeDiagnostic;
  diagnostics: DiagnosticItem[];
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
