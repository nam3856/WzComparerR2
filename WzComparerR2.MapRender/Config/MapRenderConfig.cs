using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using WzComparerR2.Config;

namespace WzComparerR2.MapRender.Config
{
    [SectionName("WcR2.MapRender")]
    public sealed class MapRenderConfig : ConfigSectionBase<MapRenderConfig>
    {
        public MapRenderConfig()
        {
            this.Volume = 1f;
            this.MuteOnLeaveFocus = true;
            this.ClipMapRegion = true;
            this.EnableMobMovement = true;
            this.CompositionOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            this.CompositionDurationSeconds = 10d;
            this.CompositionFrameRate = 30d;
            this.CompositionViewportWidth = 1920;
            this.CompositionViewportHeight = 1080;
            this.AfterEffectTemplateCreatorPath = string.Empty;
            this.AfterFxPath = string.Empty;
        }

        [ConfigurationProperty("volume")]
        public ConfigItem<float> Volume
        {
            get { return (ConfigItem<float>)this["volume"]; }
            set { this["volume"] = value; }
        }

        [ConfigurationProperty("muteOnLeaveFocus")]
        public ConfigItem<bool> MuteOnLeaveFocus
        {
            get { return (ConfigItem<bool>)this["muteOnLeaveFocus"]; }
            set { this["muteOnLeaveFocus"] = value; }
        }

        [ConfigurationProperty("defaultFontIndex")]
        public ConfigItem<int> DefaultFontIndex
        {
            get { return (ConfigItem<int>)this["defaultFontIndex"]; }
            set { this["defaultFontIndex"] = value; }
        }

        [ConfigurationProperty("clipMapRegion")]
        public ConfigItem<bool> ClipMapRegion
        {
            get { return (ConfigItem<bool>)this["clipMapRegion"]; }
            set { this["clipMapRegion"] = value; }
        }

        [ConfigurationProperty("useD2DRenderer")]
        public ConfigItem<bool> UseD2dRenderer
        {
            get { return (ConfigItem<bool>)this["useD2DRenderer"]; }
            set { this["useD2DRenderer"] = value; }
        }

        [ConfigurationProperty("topBar.Visible")]
        public ConfigItem<bool> TopBarVisible
        {
            get { return (ConfigItem<bool>)this["topBar.Visible"]; }
            set { this["topBar.Visible"] = value; }
        }

        [ConfigurationProperty("minimap.CameraRegionVisible")]
        public ConfigItem<bool> Minimap_CameraRegionVisible
        {
            get { return (ConfigItem<bool>)this["minimap.CameraRegionVisible"]; }
            set { this["minimap.CameraRegionVisible"] = value; }
        }

        [ConfigurationProperty("worldMap.UseImageNameAsInfoName")]
        public ConfigItem<bool> WorldMap_UseImageNameAsInfoName
        {
            get { return (ConfigItem<bool>)this["worldMap.UseImageNameAsInfoName"]; }
            set { this["worldMap.UseImageNameAsInfoName"] = value; }
        }

        [ConfigurationProperty("screenshotBackgroundColor")]
        public ConfigItem<string> ScreenshotBackgroundColor
        {
            get { return (ConfigItem<string>)this["screenshotBackgroundColor"]; }
            set { this["screenshotBackgroundColor"] = value; }
        }

        [ConfigurationProperty("forceCaptureWithResolution")]
        public ConfigItem<bool> ForceCaptureWithResolution
        {
            get { return (ConfigItem<bool>)this["forceCaptureWithResolution"]; }
            set { this["forceCaptureWithResolution"] = value; }
        }

        [ConfigurationProperty("showFootholdBoundary")]
        public ConfigItem<bool> ShowFootholdBoundary
        {
            get { return (ConfigItem<bool>)this["showFootholdBoundary"]; }
            set { this["showFootholdBoundary"] = value; }
        }

        [ConfigurationProperty("enableMobMovement")]
        public ConfigItem<bool> EnableMobMovement
        {
            get { return (ConfigItem<bool>)this["enableMobMovement"]; }
            set { this["enableMobMovement"] = value; }
        }

        [ConfigurationProperty("composition.outputDirectory")]
        public ConfigItem<string> CompositionOutputDirectory
        {
            get { return (ConfigItem<string>)this["composition.outputDirectory"]; }
            set { this["composition.outputDirectory"] = value; }
        }

        [ConfigurationProperty("composition.durationSeconds")]
        public ConfigItem<double> CompositionDurationSeconds
        {
            get { return (ConfigItem<double>)this["composition.durationSeconds"]; }
            set { this["composition.durationSeconds"] = value; }
        }

        [ConfigurationProperty("composition.frameRate")]
        public ConfigItem<double> CompositionFrameRate
        {
            get { return (ConfigItem<double>)this["composition.frameRate"]; }
            set { this["composition.frameRate"] = value; }
        }

        [ConfigurationProperty("composition.viewportWidth")]
        public ConfigItem<int> CompositionViewportWidth
        {
            get { return (ConfigItem<int>)this["composition.viewportWidth"]; }
            set { this["composition.viewportWidth"] = value; }
        }

        [ConfigurationProperty("composition.viewportHeight")]
        public ConfigItem<int> CompositionViewportHeight
        {
            get { return (ConfigItem<int>)this["composition.viewportHeight"]; }
            set { this["composition.viewportHeight"] = value; }
        }

        [ConfigurationProperty("composition.afterEffectTemplateCreatorPath")]
        public ConfigItem<string> AfterEffectTemplateCreatorPath
        {
            get { return (ConfigItem<string>)this["composition.afterEffectTemplateCreatorPath"]; }
            set { this["composition.afterEffectTemplateCreatorPath"] = value; }
        }

        [ConfigurationProperty("composition.afterFxPath")]
        public ConfigItem<string> AfterFxPath
        {
            get { return (ConfigItem<string>)this["composition.afterFxPath"]; }
            set { this["composition.afterFxPath"] = value; }
        }
    }
}
