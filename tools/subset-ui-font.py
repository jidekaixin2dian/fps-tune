"""Build static WPF-compatible subsets from official Noto Sans SC (fontTools build only).

python tools/subset-ui-font.py --source path/to/NotoSansSC[wght].ttf
No system font installation, network requests, or application runtime dependency.
"""
import argparse
import hashlib
import json
from pathlib import Path
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', required=True, type=Path)
args = parser.parse_args()
characters = set(range(32, 127)) | set(range(0x2000, 0x2070)) | set(range(0x3000, 0x3040))
for path in (root / 'FpsTune.Wpf').rglob('*'):
    if path.suffix not in ('.xaml', '.cs') or {'bin', 'obj'} & set(path.parts):
        continue
    characters.update(map(ord, path.read_text(encoding='utf-8-sig')))
# Optimization titles/descriptions come from the catalog, not XAML resources.
for path in (root / 'catalog').glob('*.json'):
    characters.update(map(ord, path.read_text(encoding='utf-8-sig')))
# Filter preset names are runtime filenames, and fit/avoid hints live in presets.json;
# missing glyphs fall through the composite FontBody to a different typeface mid-run.
for path in (root / 'FpsTune.Wpf' / 'Assets' / 'GameFilters').glob('*'):
    characters.update(map(ord, path.stem))
    if path.suffix == '.json':
        characters.update(map(ord, path.read_text(encoding='utf-8-sig')))
source = TTFont(args.source)
cmap = source.getBestCmap()
characters &= cmap.keys()
output = root / 'FpsTune.Wpf/Assets/Fonts'
output.mkdir(parents=True, exist_ok=True)
manifest = {'source': 'https://github.com/google/fonts/tree/main/ofl/notosanssc',
            'sourceSha256': hashlib.sha256(args.source.read_bytes()).hexdigest(),
            'sourceVersion': source['name'].getDebugName(5), 'family': 'FpsTune Text',
            'license': 'SIL Open Font License 1.1', 'codepoints': sorted(characters), 'files': []}
for weight, face in ((400, 'Regular'), (500, 'Medium'), (600, 'SemiBold')):
    font = TTFont(args.source)
    options = subset.Options()
    options.name_IDs = ['*']
    options.name_languages = ['*']
    worker = subset.Subsetter(options=options)
    worker.populate(unicodes=characters)
    worker.subset(font)
    font = instantiateVariableFont(font, {'wght': weight}, inplace=True)
    font['OS/2'].usWeightClass = weight
    # WPF selects static faces through the typographic family and subfamily names.
    names = {1: 'FpsTune Text' if weight == 400 else 'FpsTune Text ' + face,
             2: 'Regular', 3: 'FpsTuneText-' + face + '-Subset', 4: 'FpsTune Text ' + face,
             6: 'FpsTuneText-' + face, 16: 'FpsTune Text', 17: face}
    for record in font['name'].names:
        if record.nameID in names:
            record.string = names[record.nameID].encode(record.getEncoding())
    font.flavor = None
    font.recalcTimestamp = False
    path = output / ('FpsTuneText-' + face + '.ttf')
    font.save(path)
    manifest['files'].append({'name': path.name, 'bytes': path.stat().st_size,
                              'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
    print(path.name, path.stat().st_size)
(output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8', newline='\n')
