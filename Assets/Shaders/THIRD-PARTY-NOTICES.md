# Bundled mpv shaders

ElyCast ships these GLSL shaders unmodified, as source, next to the
executable (`shaders\`). Sharpen-tuned CAS/NVSharpen variants are generated
from them at runtime in `%APPDATA%\ElyCast\tools\shaders`. Each file keeps its
original copyright header.

| Files | Author | License | Source |
|---|---|---|---|
| `Anime4K_*.glsl` | bloc97 | MIT | https://github.com/bloc97/Anime4K (v4.0.1) |
| `FSR.glsl` | Advanced Micro Devices (port by agyild) | MIT | https://gist.github.com/agyild/82219c545228d70c5604f865ce0b0ce5 |
| `CAS.glsl`, `CAS-scaled.glsl` | Advanced Micro Devices (port by agyild) | MIT | https://gist.github.com/agyild/bbb4e58298b2f86aa24da3032a0d2ee6 |
| `NVScaler.glsl`, `NVSharpen.glsl` | NVIDIA Corporation (port by agyild) | MIT | https://gist.github.com/agyild/7e8951915b2bf24526a9343d951db214 |
| `FSRCNNX_x2_8-0-4-1.glsl`, `FSRCNNX_x2_16-0-4-1.glsl` | igv | LGPL-3.0-or-later | https://github.com/igv/FSRCNN-TensorFlow (release 1.1) |
| `KrigBilateral.glsl` | igv | LGPL-3.0-or-later | https://gist.github.com/igv/a015fc885d5c22e6891820ad89555637 |
| `adaptive-sharpen.glsl` | bacondither (port by igv) | BSD-2-Clause | https://gist.github.com/igv/8a77e4eb8276753b54bb94c1c50c317e |
| `ElyCast_Vibrance.glsl` | ElyCast | MPL-2.0 | this repository |

The LGPL-licensed shaders are distributed as their complete, modifiable
source; their license texts are `LGPL-3.0.txt` and `GPL-3.0.txt` in this
folder. Replacing a file in the installed `shaders\` folder is supported:
ElyCast loads whatever file is there.
