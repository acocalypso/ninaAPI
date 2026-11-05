#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using System;

namespace ninaAPI.Utility
{
    /// <summary>
    /// The pattern the API debayers an image with. Casting the profile's bayer pattern to a sensor type turned None into
    /// -1, which is no pattern at all, and Debayer throws for every sensor type but the eight 2x2 patterns: a request for an
    /// OSC camera with the pattern None failed, or logged an error and left the image as it was.
    /// </summary>
    public static class DebayerPattern
    {
        /// <summary>
        /// The profile's bayer pattern as a sensor type: None treats an OSC camera as monochrome (mono bin modes, as NINA
        /// does), Auto takes the driver's sensor type (monochrome without a connected camera), any other the pattern itself.
        /// </summary>
        public static SensorType SensorTypeFor(BayerPatternEnum pattern, CameraInfo camera)
        {
            return pattern switch
            {
                BayerPatternEnum.None => SensorType.Monochrome,
                BayerPatternEnum.Auto => camera?.Connected == true ? camera.SensorType : SensorType.Monochrome,
                _ => (SensorType)pattern,
            };
        }

        /// <summary>Whether Debayer can use the sensor type; it throws for monochrome, color, CMYG and LRGB sensors.</summary>
        public static bool CanDebayer(SensorType sensor)
        {
            return sensor is SensorType.RGGB or SensorType.RGBG or SensorType.GRGB or SensorType.GRBG
                or SensorType.GBGR or SensorType.GBRG or SensorType.BGRG or SensorType.BGGR;
        }

        /// <summary>A bayerPattern query value: a sensor type name or number that exists (Enum.Parse accepted any number).</summary>
        public static bool TryParse(string value, out SensorType sensor)
        {
            return Enum.TryParse(value, out sensor) && Enum.IsDefined(sensor);
        }
    }
}
