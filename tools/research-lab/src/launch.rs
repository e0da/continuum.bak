use crate::{flight, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, path::PathBuf};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum GraphicsApi {
    Current,
    Metal,
}
impl GraphicsApi {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Current => "current",
            Self::Metal => "metal",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum CatalogMode {
    None,
    Prepare,
    PrepareMinimal,
    Reuse,
}
impl CatalogMode {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::None => "none",
            Self::Prepare => "prepare",
            Self::PrepareMinimal => "prepare-minimal",
            Self::Reuse => "reuse",
        }
    }
    pub fn is_preparation(&self) -> bool {
        matches!(self, Self::Prepare | Self::PrepareMinimal)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum ResourceImageMode {
    None,
    Prepare,
    Reuse,
}
impl ResourceImageMode {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::None => "none",
            Self::Prepare => "prepare",
            Self::Reuse => "reuse",
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Startup {
    #[serde(default, skip_serializing_if = "is_false")]
    pub prepared_bindings: bool,
    pub unlimited: bool,
    pub reuse_bundle_data: bool,
    pub file_bundles: bool,
    pub build_cache: bool,
    pub cache: Option<PathBuf>,
    pub cached_bundles: bool,
    pub native_admission: bool,
    pub prepared_bundles: bool,
    pub catalog_mode: CatalogMode,
    pub catalog: Option<PathBuf>,
    pub texture_prefetch: bool,
    pub dds_mapped: bool,
    pub audio_prefetch: bool,
    pub readiness: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub stock_terrain_ready: bool,
    pub deferred_terrain: bool,
    pub resource_image_mode: ResourceImageMode,
    pub resource_image: Option<PathBuf>,
    pub work_scheduler: bool,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Diagnostics {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aero_step_context: Option<String>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub aero_drag_transaction: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub fixed_step_owner_attribution: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub fixed_dispatch_attribution: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ordered_body_aero_capture: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub native_assembly_scene: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub live_rigid_owner: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub zero_native_force_pruning: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scientific_attachment_nodes: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub trail_emitter: Option<TrailEmitterSelection>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub checkpoint_reload: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub convection_replacement: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cylinder_occlusion_replacement: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub docking: Option<crate::docking::Selection>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub flight_lighting: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub convection_capture: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub integrator_update_attribution: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub backend_menu_presentation: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub backend_pixel_proof_bundle: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub multiscale_scene: Option<PathBuf>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub menu_material_facts: Option<PathBuf>,
    pub profile: bool,
    pub texture_observe: bool,
    pub world_timing: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub construction_timing: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub part_construction_timing: bool,
    pub site_proof: Option<String>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TrailEmitterSelection {
    pub recording: PathBuf,
    pub part: String,
    pub mode: String,
}
fn is_false(value: &bool) -> bool {
    !*value
}

pub fn validate_construction_closure(report: &serde_json::Value) -> Result<()> {
    let failure = || {
        "construction observer did not close ownership or recorded a measured failure".to_string()
    };
    if report["cleanupStatus"] != "removed-owned-hooks"
        || report["hooksRemoved"].as_bool() != Some(true)
    {
        return Err(failure());
    }
    for row in report["scopes"].as_array().ok_or_else(failure)? {
        if row["errors"].as_u64() != Some(0) {
            return Err(failure());
        }
    }
    for row in report["children"].as_array().ok_or_else(failure)? {
        if row["errors"].as_u64() != Some(0) || row["synchronous"]["errors"].as_u64() != Some(0) {
            return Err(failure());
        }
    }
    for row in report["readiness"].as_array().ok_or_else(failure)? {
        if row["errors"].as_u64() != Some(0) || row["construction"]["errors"].as_u64() != Some(0) {
            return Err(failure());
        }
    }
    Ok(())
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResolvedLaunch {
    pub schema: String,
    #[serde(default, skip_serializing_if = "is_false")]
    pub live_play: bool,
    pub timeout_seconds: u64,
    pub graphics_api: GraphicsApi,
    pub startup: Startup,
    pub diagnostics: Diagnostics,
    pub native_asset_installation: Option<PathBuf>,
    pub flight: Option<flight::Request>,
}

impl ResolvedLaunch {
    pub fn parse(options: &BTreeMap<String, String>) -> Result<Self> {
        let boolean = |key: &str| match options.get(key).map(String::as_str) {
            None | Some("false") => Ok(false),
            Some("true") => Ok(true),
            _ => Err(format!(
                "{} must be true or false",
                key.trim_start_matches("--")
            )),
        };
        let path = |key: &str| options.get(key).map(PathBuf::from);
        let launch = Self {
            schema: "continuum-resolved-launch/v1".into(),
            live_play: boolean("--live-play")?,
            timeout_seconds: options
                .get("--timeout-seconds")
                .map(|v| v.parse::<u64>())
                .transpose()
                .map_err(|e| e.to_string())?
                .unwrap_or(300),
            graphics_api: match options.get("--graphics-api").map(String::as_str) {
                None | Some("current") => GraphicsApi::Current,
                Some("metal") => GraphicsApi::Metal,
                _ => return Err("graphics-api must be current or metal".into()),
            },
            startup: Startup {
                prepared_bindings: boolean("--startup-prepared-bindings")?,
                unlimited: boolean("--startup-unlimited")?,
                reuse_bundle_data: boolean("--startup-reuse-bundle-data")?,
                file_bundles: boolean("--startup-file-bundles")?,
                build_cache: boolean("--startup-build-cache")?,
                cache: path("--startup-cache"),
                cached_bundles: boolean("--startup-cached-bundles")?,
                native_admission: boolean("--startup-native-admission")?,
                prepared_bundles: boolean("--startup-prepared-bundles")?,
                catalog_mode: match options.get("--startup-catalog-mode").map(String::as_str) {
                    None | Some("none") => CatalogMode::None,
                    Some("prepare") => CatalogMode::Prepare,
                    Some("prepare-minimal") => CatalogMode::PrepareMinimal,
                    Some("reuse") => CatalogMode::Reuse,
                    _ => {
                        return Err(
                            "startup-catalog-mode must be none, prepare, prepare-minimal, or reuse"
                                .into(),
                        )
                    }
                },
                catalog: path("--startup-catalog"),
                texture_prefetch: boolean("--startup-texture-prefetch")?,
                dds_mapped: boolean("--startup-dds-mapped")?,
                audio_prefetch: boolean("--startup-audio-prefetch")?,
                readiness: boolean("--startup-readiness")?,
                stock_terrain_ready: boolean("--startup-stock-terrain-ready")?,
                deferred_terrain: boolean("--startup-deferred-terrain")?,
                resource_image_mode: match options
                    .get("--startup-resource-image-mode")
                    .map(String::as_str)
                {
                    None | Some("none") => ResourceImageMode::None,
                    Some("prepare") => ResourceImageMode::Prepare,
                    Some("reuse") => ResourceImageMode::Reuse,
                    _ => {
                        return Err(
                            "startup-resource-image-mode must be none, prepare, or reuse".into(),
                        )
                    }
                },
                resource_image: path("--startup-resource-image"),
                work_scheduler: boolean("--startup-work-scheduler")?,
            },
            diagnostics: Diagnostics {
                aero_step_context: options.get("--flight-aero-step-context").cloned(),
                aero_drag_transaction: boolean("--flight-aero-drag-transaction")?,
                fixed_step_owner_attribution: boolean("--flight-fixed-step-owner-attribution")?,
                fixed_dispatch_attribution: boolean("--flight-fixed-dispatch-attribution")?,
                ordered_body_aero_capture: path("--flight-ordered-body-aero-capture"),
                native_assembly_scene: path("--flight-native-assembly-scene"),
                live_rigid_owner: path("--flight-live-rigid-owner"),
                zero_native_force_pruning: options.get("--flight-zero-native-force-pruning").cloned(),
                scientific_attachment_nodes: options.get("--flight-scientific-attachment-nodes").cloned(),
                trail_emitter: options.get("--flight-trail-emitter").map(|output| Ok::<TrailEmitterSelection,String>(TrailEmitterSelection {
                    recording: PathBuf::from(output),
                    part: options.get("--flight-trail-emitter-part").ok_or("explicit root or numeric trail emitter part required")?.clone(),
                    mode: options.get("--flight-trail-emitter-mode").ok_or("explicit measured trajectory-tracer or engine-readback mode required")?.clone(),
                })).transpose()?,
                checkpoint_reload: boolean("--flight-checkpoint-reload")?,
                convection_replacement: options.get("--flight-convection-replacement").cloned(),
                cylinder_occlusion_replacement: options.get("--flight-cylinder-occlusion").cloned(),
                docking: options.get("--flight-docking").map(|value|
                    crate::docking::Selection::parse(value, boolean("--flight-docking-prepare-pair")?)
                ).transpose()?,
                flight_lighting: options.get("--flight-lighting").cloned(),
                convection_capture: path("--flight-convection-capture"),
                integrator_update_attribution: boolean("--flight-integrator-update-attribution")?,
                backend_menu_presentation: path("--backend-menu-presentation"),
                backend_pixel_proof_bundle: path("--backend-pixel-proof-bundle"),
                multiscale_scene: path("--flight-multiscale-scene"),
                menu_material_facts: path("--menu-material-facts"),
                profile: boolean("--startup-profile")?,
                texture_observe: boolean("--startup-texture-observe")?,
                world_timing: boolean("--startup-world-timing")?,
                construction_timing: boolean("--startup-construction-timing")?,
                part_construction_timing: boolean("--startup-part-construction-timing")?,
                site_proof: options.get("--startup-site-proof").cloned(),
            },
            native_asset_installation: path("--native-asset-installation"),
            flight: flight::Request::parse(options)?,
        };
        if boolean("--flight-docking-prepare-pair")? && launch.diagnostics.docking.is_none() {
            return Err("docking fixture preparation requires a selected docking pair".into());
        }
        launch.validate()?;
        Ok(launch)
    }

    pub fn validate(&self) -> Result<()> {
        let d=&self.diagnostics;
        if d.live_rigid_owner.is_some() {
            let f=self.flight.as_ref().ok_or("live rigid owner requires pinned flight")?;
            if self.live_play || f.expected_parts.is_none() || f.warp!=1 || f.atmosphere || f.seconds<=2.0 || !f.seconds.is_finite()
                || f.perturbation!="quiet" || f.callback_attribution || d.checkpoint_reload || d.docking.is_some()
                || d.aero_step_context.is_some() || d.aero_drag_transaction || d.ordered_body_aero_capture.is_some()
                || d.zero_native_force_pruning.is_some() || d.fixed_step_owner_attribution || d.fixed_dispatch_attribution
                || d.convection_replacement.is_some() || d.cylinder_occlusion_replacement.is_some() || d.integrator_update_attribution
                || d.convection_capture.is_some() || d.native_assembly_scene.is_some() {
                return Err("live rigid owner requires exclusive normal-time pinned vacuum, quiet capture longer than two simulated seconds".into());
            }
        }
        if d.native_assembly_scene.is_some() && self.flight.as_ref().is_some_and(|f| !f.seconds.is_finite() || f.seconds<=1.25) {
            return Err("native assembly capture must extend beyond the harness default pulse window (1.25 simulated seconds), including quiet captures".into());
        }
        if d.aero_step_context.is_some() || d.fixed_step_owner_attribution || d.trail_emitter.is_some() || d.ordered_body_aero_capture.is_some() || d.zero_native_force_pruning.is_some() || d.native_assembly_scene.is_some() {
            let f=self.flight.as_ref().ok_or("fixed-owner/aero/emitter requires pinned flight")?;
            if self.live_play || f.expected_parts.is_none() || f.warp!=1 || f.callback_attribution
                || d.checkpoint_reload || d.docking.is_some() || d.convection_capture.is_some() {
                return Err("fixed-owner/aero/emitter requires normal-time pinned diagnostic without broad attribution/reload/docking/capture".into());
            }
        }
        if d.fixed_dispatch_attribution && !d.fixed_step_owner_attribution {
            return Err("fixed dispatch attribution requires the bound fixed-step owner and shared phase brackets".into());
        }
        if let Some(mode) = d.zero_native_force_pruning.as_deref() {
            if !["compare", "query"].contains(&mode) || d.ordered_body_aero_capture.is_some()
                || d.aero_step_context.is_some() || d.aero_drag_transaction {
                return Err("zero native force pruning requires compare/query and exclusive original integrator ownership".into());
            }
        }
        if let Some(path) = &d.ordered_body_aero_capture {
            if d.aero_step_context.is_some() || d.aero_drag_transaction || self.live_play
                || !path.is_absolute() || path.exists()
                || !path.parent().is_some_and(|p| p.is_dir())
                || path.parent().unwrap().canonicalize().map_err(|e| e.to_string())? != path.parent().unwrap() {
                return Err("ordered body aero capture requires original provider execution and a new canonical absolute output".into());
            }
        }
        if let Some(nodes) = &d.scientific_attachment_nodes {
            if d.multiscale_scene.is_none() || nodes.is_empty() || nodes.split(',').count() > 128
                || nodes.split(',').any(|node| !node.split_once(':').is_some_and(|(owner, id)|
                    owner.parse::<u32>().is_ok_and(|v| v > 0) && !id.is_empty()
                    && id.bytes().all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b)))) {
                return Err("scientific attachment selection requires a new scene and explicit part-ID:node-ID entries".into());
            }
        }
        if let Some(mode)=&d.aero_step_context {
            if !["verify","query","compare"].contains(&mode.as_str())
                || (d.integrator_update_attribution && mode!="query")
                || d.convection_replacement.as_deref().is_some_and(|v|v!="query") {
                return Err("aero step mode requires verify/query/compare; existing narrow/convection owners require query".into());
            }
        }
        if d.aero_drag_transaction && d.aero_step_context.is_none() {
            return Err("complete drag transaction requires an explicit aero step mode".into());
        }
        if let Some(emitter)=&d.trail_emitter {
            if !(emitter.part=="root" || emitter.part.parse::<u32>().is_ok_and(|v|v>0))
                || !["trajectory-tracer","engine-readback"].contains(&emitter.mode.as_str())
                || d.multiscale_scene.is_none() || !emitter.recording.is_absolute()
                || emitter.recording.exists() || !emitter.recording.parent().is_some_and(|p|p.is_dir()) {
                return Err("emitter requires explicit part/mode, fresh cold scene and new absolute recording under existing directory".into());
            }
        }
        crate::play::validate(self)?;
        if self.schema != "continuum-resolved-launch/v1" {
            return Err("unsupported resolved launch schema".into());
        }
        if !(1..=1800).contains(&self.timeout_seconds) {
            return Err("timeout must be 1..1800 seconds".into());
        }
        let s = &self.startup;
        if let Some(mode) = &self.diagnostics.cylinder_occlusion_replacement {
            let f = self.flight.as_ref().ok_or("cylinder occlusion requires a pinned flight")?;
            if !["verify", "compare", "query"].contains(&mode.as_str()) || self.live_play
                || f.warp != 1 || f.expected_parts.is_none() || f.callback_attribution
                || self.diagnostics.checkpoint_reload || self.diagnostics.docking.is_some()
                || self.diagnostics.convection_capture.is_some()
            {
                return Err("cylinder occlusion requires verify, compare or query in a normal-time pinned flight without broad attribution/reload/docking/capture".into());
            }
        }
        if self.diagnostics.checkpoint_reload {
            let f = self.flight.as_ref().ok_or("checkpoint reload requires a pinned flight")?;
            if self.live_play || f.warp != 1 || f.atmosphere || f.entry_target_altitude.is_some()
                || f.perturbation != "quiet" || f.expected_parts.is_none()
                || f.native_sample || f.callback_attribution || f.camera_chain || f.world_draw.is_some()
                || f.gpu_timing_library.is_some() || f.aero_batch || f.aero_field || f.part_batch
                || f.hide_vessel || f.no_aero_fx || f.part_reference || f.part_retained
                || f.aero_field_bundle.is_some() || f.part_shader_bundle.is_some() || f.mechjeb_provider_plan
                || self.diagnostics.docking.is_some() || self.diagnostics.flight_lighting.is_some()
                || self.diagnostics.convection_capture.is_some() || self.diagnostics.convection_replacement.is_some()
                || self.diagnostics.integrator_update_attribution || self.diagnostics.multiscale_scene.is_some()
            {
                return Err("checkpoint reload requires an exclusive normal-time stock orbital flight and explicit part count".into());
            }
        }
        if let Some(mode) = &self.diagnostics.convection_replacement {
            let f = self.flight.as_ref().ok_or("convection replacement requires flight")?;
            if !["query", "verify"].contains(&mode.as_str()) || !f.atmosphere || f.warp != 1
                || self.diagnostics.checkpoint_reload || self.diagnostics.docking.is_some()
                || self.diagnostics.convection_capture.is_some()
                || (mode == "verify" && (f.callback_attribution || self.diagnostics.integrator_update_attribution)) || self.live_play
            {
                return Err("convection replacement requires query or verify in normal-time atmospheric flight; only query admits callback attribution".into());
            }
        }
        if let Some(docking) = &self.diagnostics.docking {
            docking.validate()?;
            let f = self.flight.as_ref().ok_or("docking requires a pinned flight checkpoint")?;
            if self.live_play || f.warp != 1 || f.atmosphere || f.entry_target_altitude.is_some()
                || f.perturbation != "quiet" || f.expected_parts.is_none()
                || f.native_sample || f.callback_attribution || f.camera_chain || f.world_draw.is_some()
                || f.gpu_timing_library.is_some() || f.aero_batch || f.aero_field || f.part_batch
                || f.hide_vessel || f.no_aero_fx || f.part_reference || f.part_retained
                || f.aero_field_bundle.is_some() || f.part_shader_bundle.is_some() || f.mechjeb_provider_plan
                || self.diagnostics.flight_lighting.is_some() || self.diagnostics.convection_capture.is_some()
                || self.diagnostics.integrator_update_attribution || self.diagnostics.multiscale_scene.is_some()
                || self.diagnostics.convection_replacement.is_some() || self.diagnostics.checkpoint_reload
            {
                return Err("docking is an exclusive normal-time stock-rendered diagnostic with explicit initial part count".into());
            }
        }
        if let Some(mode) = &self.diagnostics.flight_lighting {
            if !["frontlit", "side-lit", "backlit"].contains(&mode.as_str())
                || self.flight.as_ref().is_none_or(|flight| flight.entry_target_altitude.is_none())
            {
                return Err("physical flight lighting requires frontlit, side-lit or backlit and an explicit entry target; sunward placement is a declared workload treatment".into());
            }
        }
        if let Some(path) = &self.diagnostics.convection_capture {
            if self.flight.is_none()
                || self.diagnostics.integrator_update_attribution
                || self
                    .flight
                    .as_ref()
                    .is_some_and(|flight| flight.callback_attribution)
                || !path.is_absolute()
                || path.exists()
                || !path.parent().is_some_and(|parent| parent.is_dir())
                || path
                    .parent()
                    .unwrap()
                    .canonicalize()
                    .map_err(|e| e.to_string())?
                    != path.parent().unwrap()
            {
                return Err("convection capture requires exclusive flight and a new canonical absolute output".into());
            }
        }
        if self.diagnostics.integrator_update_attribution
            && (self.flight.is_none()
                || self
                    .flight
                    .as_ref()
                    .is_some_and(|flight| flight.callback_attribution))
        {
            return Err("integrator Update attribution requires flight and excludes broad callback attribution".into());
        }
        if let Some(path) = &self.diagnostics.backend_menu_presentation {
            if self.flight.is_some()
                || self.graphics_api != GraphicsApi::Metal
                || self.diagnostics.backend_pixel_proof_bundle.is_some()
                || s.catalog_mode.is_preparation()
                || s.resource_image_mode == ResourceImageMode::Prepare
                || s.build_cache
            {
                return Err("backend menu presentation requires an exclusive Metal menu run without preparation".into());
            }
            if !path.is_absolute()
                || !path.is_file()
                || path.canonicalize().map_err(|e| e.to_string())? != *path
            {
                return Err(
                    "backend menu presentation requires a canonical absolute recipe file".into(),
                );
            }
        }
        if let Some(path) = &self.diagnostics.backend_pixel_proof_bundle {
            if self.flight.is_some()
                || self.graphics_api != GraphicsApi::Metal
                || matches!(
                    s.catalog_mode,
                    CatalogMode::Prepare | CatalogMode::PrepareMinimal
                )
                || s.resource_image_mode == ResourceImageMode::Prepare
                || s.build_cache
            {
                return Err(
                    "backend pixel proof requires a Metal menu-only launch without preparation"
                        .into(),
                );
            }
            if !path.is_absolute()
                || !path.is_file()
                || path.canonicalize().map_err(|e| e.to_string())? != *path
            {
                return Err(
                    "backend pixel proof bundle requires a canonical absolute existing file".into(),
                );
            }
        }
        if self.diagnostics.construction_timing
            && (!s.work_scheduler || !self.diagnostics.world_timing || self.flight.is_some())
        {
            return Err("startup construction timing requires work-scheduler, world-timing and a startup-only run".into());
        }
        if self.diagnostics.part_construction_timing && !self.diagnostics.world_timing {
            return Err("part construction timing requires world-timing".into());
        }
        for path in [
            &self.diagnostics.multiscale_scene,
            &self.diagnostics.native_assembly_scene,
            &self.diagnostics.live_rigid_owner,
            &self.diagnostics.menu_material_facts,
        ]
        .into_iter()
        .flatten()
        {
            if self.flight.is_none()
                || !path.is_absolute()
                || path.exists()
                || !path.parent().is_some_and(|parent| parent.is_dir())
            {
                return Err("scene/material capture requires flight and a new absolute output under an existing directory".into());
            }
        }
        if self.diagnostics.multiscale_scene.is_some()
            && !self
                .flight
                .as_ref()
                .is_some_and(|flight| flight.part_batch && flight.part_mapped_only)
        {
            return Err("multiscale capture requires admitted mapped part batching".into());
        }
        if s.deferred_terrain && !s.readiness {
            return Err("deferred terrain requires startup-readiness".into());
        }
        if s.stock_terrain_ready && (!s.readiness || s.deferred_terrain) {
            return Err("stock terrain readiness requires startup-readiness and original synchronous terrain construction".into());
        }
        if (s.resource_image_mode != ResourceImageMode::None) != s.resource_image.is_some() {
            return Err("resource image mode requires one private image path".into());
        }
        if (s.catalog_mode != CatalogMode::None) != s.catalog.is_some() {
            return Err("catalog prepare/reuse requires one private catalog path".into());
        }
        for (path, label) in [
            (&s.resource_image, "resource image"),
            (&s.catalog, "catalog"),
            (&s.cache, "startup cache"),
        ] {
            if path.as_ref().is_some_and(|v| !v.is_absolute()) {
                return Err(format!("{label} requires an absolute private path"));
            }
        }
        if s.resource_image_mode != ResourceImageMode::None && (s.texture_prefetch || s.dds_mapped)
        {
            return Err("resource image replaces the texture loader; select it independently of texture seam probes".into());
        }
        if (s.build_cache || s.cached_bundles) && s.cache.is_none() {
            return Err("cache preparation requires a private --startup-cache directory".into());
        }
        if s.build_cache && s.cached_bundles {
            return Err("cache preparation and cached loading require separate runs".into());
        }
        if s.native_admission && !s.cached_bundles {
            return Err("native admission requires cached bundle loading".into());
        }
        if s.prepared_bundles && !(s.cached_bundles && s.native_admission) {
            return Err(
                "prepared bundle loading requires cached bundles and native admission".into(),
            );
        }
        if s.cached_bundles && s.file_bundles {
            return Err("select one bundle load candidate".into());
        }
        let preparations = u8::from(s.build_cache)
            + u8::from(s.catalog_mode.is_preparation())
            + u8::from(s.resource_image_mode == ResourceImageMode::Prepare);
        if preparations > 1 {
            return Err("resource preparation modes require separate processes".into());
        }
        if self.flight.is_some() && preparations > 0 {
            return Err("flight capture cannot share a resource preparation run".into());
        }
        if self
            .diagnostics
            .site_proof
            .as_deref()
            .is_some_and(|v| v != "Desert_Airfield")
        {
            return Err("startup-site-proof supports Desert_Airfield".into());
        }
        if let Some(request) = &self.flight {
            let original = serde_json::to_value(request).map_err(|e| e.to_string())?;
            let mut options = BTreeMap::new();
            for (field, key) in FLIGHT_KEYS {
                let value = &original[*field];
                if !value.is_null() {
                    let text = value
                        .as_str()
                        .map(str::to_string)
                        .unwrap_or_else(|| value.to_string());
                    options.insert(format!("--flight-{key}"), text);
                }
            }
            let reparsed =
                flight::Request::parse(&options)?.ok_or("flight selection did not replay")?;
            if serde_json::to_value(reparsed).map_err(|e| e.to_string())? != original {
                return Err("flight request is not a normalized legacy selection".into());
            }
        }
        Ok(())
    }

    // Selection identity binds configuration, not asset contents or successful admission.
    pub fn identity_json(&self) -> Result<String> {
        self.validate()?;
        serde_json::to_string(self).map_err(|e| e.to_string())
    }
    pub fn identity_sha256(&self) -> Result<String> {
        Ok(format!(
            "{:x}",
            Sha256::digest(self.identity_json()?.as_bytes())
        ))
    }
    pub fn parse_from_json(json: &str) -> Result<Self> {
        let launch: Self = serde_json::from_str(json).map_err(|e| e.to_string())?;
        // Request is a legacy DTO without deny_unknown_fields; reject unknown keys here.
        let raw: serde_json::Value = serde_json::from_str(json).map_err(|e| e.to_string())?;
        if let Some(fields) = raw.get("flight").and_then(serde_json::Value::as_object) {
            if fields
                .keys()
                .any(|key| !FLIGHT_KEYS.iter().any(|(field, _)| key.as_str() == *field))
            {
                return Err("unknown flight request field".into());
            }
        }
        launch.validate()?;
        Ok(launch)
    }
}

const FLIGHT_KEYS: &[(&str, &str)] = &[
    ("expectedParts", "expected-parts"),
    ("researchPanel", "research-panel"),
    ("localCamera", "local-camera"),
    ("save", "save"),
    ("checkpoint", "checkpoint"),
    ("maximumDelta", "max-delta"),
    ("seconds", "seconds"),
    ("settleSeconds", "settle-seconds"),
    ("cameraChain", "camera-chain"),
    ("worldDraw", "world-draw"),
    ("warp", "warp"),
    ("atmosphere", "atmosphere"),
    ("resolution", "resolution"),
    ("noShadows", "no-shadows"),
    ("noAa", "no-aa"),
    ("nativeSample", "native-sample"),
    ("gpuTimingLibrary", "gpu-timing-library"),
    ("perturbation", "perturbation"),
    ("pulseDeltaV", "pulse-delta-v"),
    ("callbackAttribution", "callback-attribution"),
    ("mechjebProviderPlan", "mechjeb-provider-plan"),
    ("hideVessel", "hide-vessel"),
    ("noAeroFx", "no-aero-fx"),
    ("aeroQuality", "aero-quality"),
    ("aeroBatch", "aero-batch"),
    ("aeroField", "aero-field"),
    ("aeroFieldBundle", "aero-field-bundle"),
    ("partBatch", "part-batch"),
    ("partMappedOnly", "part-mapped-only"),
    ("partReference", "part-reference"),
    ("partRetained", "part-retained"),
    ("partShaderBundle", "part-shader-bundle"),
    ("partStandardFallback", "part-standard-fallback"),
    ("screenshot", "screenshot"),
    ("entryTargetAltitude", "entry-target-altitude"),
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn live_rigid_owner_freezes_vacuum_selection_and_rejects_overlap() {
        let directory=tempfile::tempdir().unwrap();
        let output=directory.path().canonicalize().unwrap().join("rigid.json");
        let mut input=options(&[("--flight-save","lab"),("--flight-checkpoint","checkpoint"),("--flight-expected-parts","512"),("--flight-seconds","3"),("--flight-perturbation","quiet")]);
        input.insert("--flight-live-rigid-owner".into(),output.to_str().unwrap().into());
        let launch=ResolvedLaunch::parse(&input).unwrap();
        let replay=ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap()).unwrap();
        assert_eq!(replay.diagnostics.live_rigid_owner,Some(output.clone()));
        input.insert("--flight-atmosphere".into(),"true".into()); assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-atmosphere");
        input.insert("--flight-aero-step-context".into(),"query".into()); assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-aero-step-context");
        std::fs::write(output,b"existing").unwrap(); assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn native_assembly_capture_rejects_short_windows_before_boot_and_freezes_selection() {
        let directory=tempfile::tempdir().unwrap();
        let output=directory.path().canonicalize().unwrap().join("physics");
        let mut input=options(&[("--flight-save","lab"),("--flight-checkpoint","checkpoint"),("--flight-expected-parts","512")]);
        input.insert("--flight-native-assembly-scene".into(),output.to_str().unwrap().into());
        input.insert("--flight-seconds".into(),"1".into());
        assert!(ResolvedLaunch::parse(&input).err().unwrap().contains("pulse window"));
        input.insert("--flight-seconds".into(),"3".into());
        let launch=ResolvedLaunch::parse(&input).unwrap();
        let replay=ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap()).unwrap();
        assert_eq!(replay.diagnostics.native_assembly_scene,Some(output.clone()));
        std::fs::create_dir(&output).unwrap();
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn ordered_capture_replays_explicit_ownership_and_rejects_provider_replacement() {
        let directory = tempfile::tempdir().unwrap();
        let output = directory.path().canonicalize().unwrap().join("original-commands.json");
        let mut input = options(&[("--flight-save", "lab"), ("--flight-checkpoint", "checkpoint"),
            ("--flight-expected-parts", "512"), ("--flight-atmosphere", "true")]);
        input.insert("--flight-ordered-body-aero-capture".into(), output.to_str().unwrap().into());
        let capture = ResolvedLaunch::parse(&input).unwrap();
        let replay = ResolvedLaunch::parse_from_json(&capture.identity_json().unwrap()).unwrap();
        assert_eq!(replay.diagnostics.ordered_body_aero_capture, Some(output.clone()));
        input.insert("--flight-aero-step-context".into(), "query".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-aero-step-context");
        input.insert("--flight-fixed-dispatch-attribution".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--flight-fixed-step-owner-attribution".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_ok());
        input.insert("--flight-zero-native-force-pruning".into(), "compare".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-ordered-body-aero-capture");
        let pruning = ResolvedLaunch::parse(&input).unwrap();
        let replay = ResolvedLaunch::parse_from_json(&pruning.identity_json().unwrap()).unwrap();
        assert_eq!(replay.diagnostics.zero_native_force_pruning.as_deref(), Some("compare"));
        input.insert("--flight-zero-native-force-pruning".into(), "unknown".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-zero-native-force-pruning");
        input.insert("--flight-ordered-body-aero-capture".into(), output.to_str().unwrap().into());
        std::fs::write(output, b"already-owned").unwrap();
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn cylinder_treatment_is_frozen_and_rejects_unknown_or_conflicting_modes() {
        let mut input=options(&[("--flight-save","lab"),("--flight-checkpoint","checkpoint"),
            ("--flight-expected-parts","512"),("--flight-cylinder-occlusion","compare"),
            ("--flight-integrator-update-attribution","true")]);
        let launch=ResolvedLaunch::parse(&input).unwrap();
        let replay=ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap()).unwrap();
        assert_eq!(replay.diagnostics.cylinder_occlusion_replacement.as_deref(),Some("compare"));
        input.insert("--flight-cylinder-occlusion".into(),"unknown".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--flight-cylinder-occlusion".into(),"query".into());
        input.insert("--flight-callback-attribution".into(),"true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn query_allows_each_attribution_but_verify_and_combined_observers_stay_exclusive() {
        let mut input = options(&[
            ("--flight-save", "lab"), ("--flight-checkpoint", "checkpoint"),
            ("--flight-expected-parts", "512"), ("--flight-atmosphere", "true"),
            ("--flight-convection-replacement", "query"), ("--flight-callback-attribution", "true"),
        ]);
        let launch = ResolvedLaunch::parse(&input).unwrap();
        assert!(launch.flight.as_ref().unwrap().callback_attribution);
        assert_eq!(launch.diagnostics.convection_replacement.as_deref(), Some("query"));
        let replay = ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap()).unwrap();
        assert!(replay.flight.as_ref().unwrap().callback_attribution);
        assert_eq!(replay.diagnostics.convection_replacement.as_deref(), Some("query"));
        input.insert("--flight-convection-replacement".into(), "verify".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--flight-convection-replacement".into(), "query".into());
        input.insert("--flight-integrator-update-attribution".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-callback-attribution");
        assert!(ResolvedLaunch::parse(&input).is_ok());
        input.insert("--flight-convection-replacement".into(), "verify".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn replacement_and_reload_admit_only_their_owned_flight_boundary() {
        let mut input = BTreeMap::from([
            ("--flight-save".into(), "lab".into()),
            ("--flight-checkpoint".into(), "checkpoint".into()),
            ("--flight-expected-parts".into(), "512".into()),
            ("--flight-convection-replacement".into(), "verify".into()),
        ]);
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--flight-atmosphere".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_ok());
        input.insert("--flight-convection-replacement".into(), "other".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-convection-replacement");
        input.insert("--flight-checkpoint-reload".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-atmosphere");
        assert!(ResolvedLaunch::parse(&input).is_ok());
        input.insert("--flight-warp".into(), "4".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.remove("--flight-warp");
        input.insert("--flight-part-batch".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    fn options(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect()
    }
    #[test]
    fn stock_terrain_wait_requires_same_epoch_owner() {
        let legacy = ResolvedLaunch::parse(&BTreeMap::new())
            .unwrap()
            .identity_json()
            .unwrap();
        assert!(!legacy.contains("stockTerrainReady"));
        let mut input = options(&[("--startup-stock-terrain-ready", "true")]);
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--startup-readiness".into(), "true".into());
        let identity = ResolvedLaunch::parse(&input)
            .unwrap()
            .identity_json()
            .unwrap();
        assert!(
            ResolvedLaunch::parse_from_json(&identity)
                .unwrap()
                .startup
                .stock_terrain_ready
        );
        input.insert("--startup-deferred-terrain".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn new_owners_preserve_legacy_profiles_and_require_real_consumers() {
        let legacy = ResolvedLaunch::parse(&BTreeMap::new()).unwrap();
        let identity = legacy.identity_json().unwrap();
        assert!(!identity.contains("preparedBindings"));
        assert!(!identity.contains("backendMenuPresentation"));
        assert_eq!(
            identity,
            ResolvedLaunch::parse_from_json(&identity)
                .unwrap()
                .identity_json()
                .unwrap()
        );
        let mut input = options(&[("--startup-prepared-bindings", "true")]);
        assert!(
            ResolvedLaunch::parse(&input)
                .unwrap()
                .startup
                .prepared_bindings
        );
        input.insert("--startup-prepared-bindings".into(), "yes".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input = options(&[
            ("--flight-save", "scenarios"),
            ("--flight-checkpoint", "station"),
        ]);
        let flight_legacy = ResolvedLaunch::parse(&input)
            .unwrap()
            .identity_json()
            .unwrap();
        assert!(!flight_legacy.contains("mechjebProviderPlan"));
        input.insert("--flight-mechjeb-provider-plan".into(), "true".into());
        let flight = ResolvedLaunch::parse(&input).unwrap();
        assert!(flight
            .flight
            .as_ref()
            .unwrap()
            .arguments()
            .contains(&"--continuum-mechjeb-provider-plan".into()));
        assert_eq!(
            flight.identity_json().unwrap(),
            ResolvedLaunch::parse_from_json(&flight.identity_json().unwrap())
                .unwrap()
                .identity_json()
                .unwrap()
        );
        let root = tempfile::tempdir().unwrap();
        let recipe = root.path().canonicalize().unwrap().join("menu.json");
        std::fs::write(&recipe, "{}").unwrap();
        input = options(&[("--backend-menu-presentation", recipe.to_str().unwrap())]);
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--graphics-api".into(), "metal".into());
        assert!(ResolvedLaunch::parse(&input).is_ok());
        input.insert("--flight-save".into(), "scenarios".into());
        input.insert("--flight-checkpoint".into(), "station".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn construction_requires_owned_startup_and_preserves_legacy_identity() {
        let legacy = ResolvedLaunch::parse(&BTreeMap::new()).unwrap();
        assert!(!legacy
            .identity_json()
            .unwrap()
            .contains("constructionTiming"));
        assert_eq!(
            legacy.identity_json().unwrap(),
            ResolvedLaunch::parse_from_json(&legacy.identity_json().unwrap())
                .unwrap()
                .identity_json()
                .unwrap()
        );
        let mut input = options(&[("--startup-construction-timing", "true")]);
        assert!(ResolvedLaunch::parse(&input).is_err());
        let closed = serde_json::json!({"cleanupStatus":"removed-owned-hooks", "hooksRemoved":true,
            "scopes":[{"errors":0}], "children":[{"errors":0,"synchronous":{"errors":0}}],
            "readiness":[{"errors":0,"construction":{"errors":0}}]});
        assert!(validate_construction_closure(&closed).is_ok());
        for path in [
            "/hooksRemoved",
            "/scopes/0/errors",
            "/children/0/errors",
            "/children/0/synchronous/errors",
            "/readiness/0/errors",
            "/readiness/0/construction/errors",
        ] {
            let mut corrupt = closed.clone();
            *corrupt.pointer_mut(path).unwrap() = if path == "/hooksRemoved" {
                serde_json::json!(false)
            } else {
                serde_json::json!(1)
            };
            assert!(
                validate_construction_closure(&corrupt).is_err(),
                "accepted corrupt closure {path}"
            );
        }
        input.insert("--startup-work-scheduler".into(), "true".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
        input.insert("--startup-world-timing".into(), "true".into());
        let launch = ResolvedLaunch::parse(&input).unwrap();
        assert!(
            ResolvedLaunch::parse_from_json(&launch.identity_json().unwrap())
                .unwrap()
                .diagnostics
                .construction_timing
        );
        input.insert("--flight-save".into(), "Research".into());
        input.insert("--flight-checkpoint".into(), "station".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn preparation_cannot_capture_or_compete_for_process_exit() {
        let mut input = options(&[
            ("--startup-catalog-mode", "prepare-minimal"),
            ("--startup-catalog", "/private/catalog.json"),
            ("--flight-save", "Research"),
            ("--flight-checkpoint", "station"),
        ]);
        assert!(ResolvedLaunch::parse(&input)
            .err()
            .unwrap()
            .contains("preparation"));
        input.remove("--flight-save");
        input.remove("--flight-checkpoint");
        input.insert("--startup-build-cache".into(), "true".into());
        input.insert("--startup-cache".into(), "/private/cache".into());
        assert!(ResolvedLaunch::parse(&input).is_err());
    }
    #[test]
    fn texture_and_bundle_producers_have_one_owner() {
        for option in ["--startup-texture-prefetch", "--startup-dds-mapped"] {
            assert!(ResolvedLaunch::parse(&options(&[
                ("--startup-resource-image-mode", "reuse"),
                ("--startup-resource-image", "/private/image"),
                (option, "true")
            ]))
            .is_err());
        }
        assert!(ResolvedLaunch::parse(&options(&[
            ("--startup-cached-bundles", "true"),
            ("--startup-cache", "/private/cache"),
            ("--startup-file-bundles", "true")
        ]))
        .is_err());
    }
    #[test]
    fn rendering_dependencies_and_frozen_ranges_survive_replay() {
        assert!(ResolvedLaunch::parse(&options(&[
            ("--flight-save", "Research"),
            ("--flight-checkpoint", "station"),
            ("--flight-part-retained", "true")
        ]))
        .is_err());
        let launch = ResolvedLaunch::parse(&options(&[
            ("--flight-save", "Research"),
            ("--flight-checkpoint", "station"),
        ]))
        .unwrap();
        let mut json = serde_json::to_value(&launch).unwrap();
        json["flight"]["seconds"] = serde_json::json!(999);
        assert!(ResolvedLaunch::parse_from_json(&json.to_string()).is_err());
        json["flight"]["seconds"] = serde_json::json!(10);
        json["flight"]["unknown"] = serde_json::json!(true);
        assert!(ResolvedLaunch::parse_from_json(&json.to_string()).is_err());
    }
    #[test]
    fn equivalent_defaults_share_selection_identity_without_asset_claims() {
        let default = ResolvedLaunch::parse(&BTreeMap::new()).unwrap();
        let explicit = ResolvedLaunch::parse(&options(&[
            ("--startup-profile", "false"),
            ("--graphics-api", "current"),
            ("--startup-catalog-mode", "none"),
            ("--timeout-seconds", "300"),
        ]))
        .unwrap();
        assert_eq!(
            default.identity_sha256().unwrap(),
            explicit.identity_sha256().unwrap()
        );
        assert_eq!(
            default.identity_json().unwrap(),
            ResolvedLaunch::parse_from_json(&default.identity_json().unwrap())
                .unwrap()
                .identity_json()
                .unwrap()
        );
        assert!(ResolvedLaunch::parse(&options(&[("--startup-unlimited", "yes")])).is_err());
        assert!(
            ResolvedLaunch::parse(&options(&[("--startup-prepared-bundles", "true")])).is_err()
        );
    }
    #[test]
    fn aero_and_bulk_owner_treatments_are_frozen_and_reject_orphans() {
        let seed=options(&[("--flight-save","Research"),("--flight-checkpoint","station"),("--flight-expected-parts","512"),("--flight-atmosphere","true")]);
        let control=ResolvedLaunch::parse(&seed).unwrap();
        let mut orphan=seed.clone(); orphan.insert("--flight-aero-drag-transaction".into(),"true".into());
        assert!(ResolvedLaunch::parse(&orphan).is_err());
        orphan.insert("--flight-aero-step-context".into(),"query".into());
        let candidate=ResolvedLaunch::parse(&orphan).unwrap();
        assert_ne!(candidate.identity_sha256().unwrap(),control.identity_sha256().unwrap());
        assert!(candidate.diagnostics.aero_drag_transaction);
        orphan.insert("--flight-fixed-step-owner-attribution".into(),"true".into());
        let observed=ResolvedLaunch::parse(&orphan).unwrap();
        assert_ne!(observed.identity_sha256().unwrap(),candidate.identity_sha256().unwrap());
        assert_eq!(observed.identity_sha256().unwrap(),ResolvedLaunch::parse_from_json(&observed.identity_json().unwrap()).unwrap().identity_sha256().unwrap());
    }
}
