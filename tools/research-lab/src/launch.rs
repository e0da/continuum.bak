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
    pub deferred_terrain: bool,
    pub resource_image_mode: ResourceImageMode,
    pub resource_image: Option<PathBuf>,
    pub work_scheduler: bool,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Diagnostics {
    pub profile: bool,
    pub texture_observe: bool,
    pub world_timing: bool,
    pub site_proof: Option<String>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResolvedLaunch {
    pub schema: String,
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
                profile: boolean("--startup-profile")?,
                texture_observe: boolean("--startup-texture-observe")?,
                world_timing: boolean("--startup-world-timing")?,
                site_proof: options.get("--startup-site-proof").cloned(),
            },
            native_asset_installation: path("--native-asset-installation"),
            flight: flight::Request::parse(options)?,
        };
        launch.validate()?;
        Ok(launch)
    }

    pub fn validate(&self) -> Result<()> {
        if self.schema != "continuum-resolved-launch/v1" {
            return Err("unsupported resolved launch schema".into());
        }
        if !(1..=1800).contains(&self.timeout_seconds) {
            return Err("timeout must be 1..1800 seconds".into());
        }
        let s = &self.startup;
        if s.deferred_terrain && !s.readiness {
            return Err("deferred terrain requires startup-readiness".into());
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
    fn options(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect()
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
}
