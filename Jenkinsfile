// Release CI: builds CivVModInstaller.exe on the maintainer's Windows build
// VM and publishes GitHub releases.
//   push to main     -> stable release vX.Y, marked Latest (X from VERSION; every stable bumps Y)
//   push to release  -> pre-release vX.Y-betaN (Y = the next stable); updaters
//                       ignore it, releases/latest skips pre-releases
// Nothing is built when only non-shipping files (ticker.txt, docs) changed.
// Builds started by branch indexing never publish.
// To build it yourself, see build.bat / build.sh.
pipeline {
    agent any
    options {
        disableConcurrentBuilds()
        timeout(time: 60, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20', artifactNumToKeepStr: '5'))
    }
    stages {
        stage('Build and release') {
            steps {
                script {
                    def publish = !currentBuild.getBuildCauses('jenkins.branch.BranchEventCause').isEmpty() ||
                                  !currentBuild.getBuildCauses('hudson.model.Cause$UserIdCause').isEmpty()
                    def kind = env.BRANCH_NAME == 'main' ? 'stable' : 'beta'
                    withCredentials([usernamePassword(credentialsId: 'OBLASTWAR_GITHUB_LOGIN',
                                         usernameVariable: 'GH_USER', passwordVariable: 'GITHUB_TOKEN'),
                                     file(credentialsId: 'civ5-installer-beat-mp3', variable: 'BEAT_MP3')]) {
                        sh """
                            set -eu
                            ov="\$(mktemp -d)"
                            trap 'rm -rf "\$ov"' EXIT
                            cp "\$BEAT_MP3" "\$ov/beat.mp3"
                            ci-release --repo OBLASTWAR/civ5-mod-installer --kind ${kind} \\
                                --title 'Civ V Mod Installer' --ci-script ci/winbuild.sh \\
                                --asset CivVModInstaller.exe --asset civ5-mod --asset version.txt \\
                                --exclude '*.md' --exclude Jenkinsfile --exclude ticker.txt --exclude .gitignore \\
                                --overlay "\$ov" --keep-betas 1 ${publish ? '--publish' : ''}
                        """
                    }
                }
            }
        }
    }
    post {
        always { archiveArtifacts artifacts: 'out/**', allowEmptyArchive: true }
    }
}
