package projecttestutil

import (
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/project"
)

func CSharpSetup(t *testing.T, files map[string]any) (*project.Session, *SessionUtils) {
    session, utils := Setup(files); project.CSharpRegisterSession(t.Name(), session); return session, utils
}
func CSharpSetupWithOptions(t *testing.T, files map[string]any, options *project.SessionOptions) (*project.Session, *SessionUtils) {
    session, utils := SetupWithOptions(files, options); project.CSharpRegisterSession(t.Name(), session); return session, utils
}
func CSharpSetupWithTypingsInstaller(t *testing.T, files map[string]any, options *TypingsInstallerOptions) (*project.Session, *SessionUtils) {
    session, utils := SetupWithTypingsInstaller(files, options)
    recordNpm(t, session, utils, options); return session, utils
}
func CSharpSetupWithOptionsAndTypingsInstaller(t *testing.T, files map[string]any, options *project.SessionOptions, typings *TypingsInstallerOptions) (*project.Session, *SessionUtils) {
    session, utils := SetupWithOptionsAndTypingsInstaller(files, options, typings)
    recordNpm(t, session, utils, typings); return session, utils
}
func recordNpm(t *testing.T, session *project.Session, utils *SessionUtils, options *TypingsInstallerOptions) {
    project.CSharpRegisterSession(t.Name(), session)
    if options == nil { return }
    project.CSharpRegisterNpm(session, utils.createTypesRegistryFileContent(), options.PackageToFile)
    t.Cleanup(func() {
        session.WaitForBackgroundTasks()
        calls := []map[string]any{}
        for _, call := range utils.NpmExecutor().NpmInstallCalls() { calls = append(calls, map[string]any{"cwd": call.Cwd, "args": call.Args}) }
        project.CSharpRecordNpmCalls(session, calls)
    })
}

func CSharpRecordNpm(t *testing.T, session *project.Session, utils *SessionUtils, options *TypingsInstallerOptions) {
    recordNpm(t, session, utils, options)
}
