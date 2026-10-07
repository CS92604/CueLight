import sys
from pathlib import Path

# Let tests import sibling helper modules (e.g. `from test_suggest import FakeClient`).
sys.path.insert(0, str(Path(__file__).parent))
