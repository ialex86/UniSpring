# Test host project

`UnityProject` is a minimal Unity project used only to compile the package and
run its EditMode tests. Unity does not import folders ending in `~`, so this
project never becomes part of the package.

Unity refuses to open a project path that contains `~`. Copy the template
outside the package and point `Packages/manifest.json` at the package:

```bash
Tests~/run-tests.sh /path/to/Unity
```

The script does this automatically in a temporary directory. To run the tests
by hand:

```bash
cp -R Tests~/UnityProject /tmp/UniSpringTestProject
# set "com.ialex86.unispring": "file:/absolute/path/to/UniSpring" in the manifest
Unity -batchmode -nographics -projectPath /tmp/UniSpringTestProject \
      -runTests -testPlatform editmode -testResults results.xml
```

`com.unity.test-framework` is required in the host manifest; it is not a
dependency of the package itself.
