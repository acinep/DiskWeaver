import React, { useCallback, useEffect, useState } from "react";
import { Page, PageSection, Title, Tabs, Tab, TabTitleText, Button } from "@patternfly/react-core";
import { apiGetJson } from "../api.js";
import { ErrorBanner } from "./ErrorBanner.jsx";
import { PoolsTable } from "./PoolsTable.jsx";
import { DiskInventory, countAvailableDisks } from "./DiskInventory.jsx";
import { CreateExpandWizard } from "./CreateExpandWizard.jsx";
import { ReassembleButton } from "./ReassembleButton.jsx";

// onLogout is only ever passed by the standalone build (src/standalone/StandaloneApp.jsx) --
// Cockpit's own session/logout is handled by the Cockpit shell itself, entirely outside this
// plugin, so its entry point (src/app.jsx) never passes it and this stays undefined there.
export function App({ onLogout } = {}) {
    const [pools, setPools] = useState([]);
    const [disks, setDisks] = useState([]);
    const [poolsLoading, setPoolsLoading] = useState(true);
    const [disksLoading, setDisksLoading] = useState(true);
    const [error, setError] = useState(null);
    // null = wizard closed; "" = create-new-pool flow; poolName = expand flow
    const [wizardTarget, setWizardTarget] = useState(null);
    const [activeTabKey, setActiveTabKey] = useState("pools");

    const loadPools = useCallback(() => {
        setPoolsLoading(true);
        return apiGetJson("/pools")
            .then(setPools)
            .catch(err => setError(`GET /pools failed: ${err.message || err}`))
            .finally(() => setPoolsLoading(false));
    }, []);

    const loadDisks = useCallback(() => {
        setDisksLoading(true);
        return apiGetJson("/inventory")
            .then(setDisks)
            .catch(err => setError(`GET /inventory failed: ${err.message || err}`))
            .finally(() => setDisksLoading(false));
    }, []);

    // Disk inventory and pools are refreshed independently, each with their own
    // button -- collapsing these into one "refresh everything" action was a
    // real bug (see docs/cockpit-plugin.md bug #6): disks attached/created
    // after page load would show stale/missing until explicitly re-fetched.
    useEffect(() => {
        loadPools();
        loadDisks();
    }, [loadPools, loadDisks]);

    function closeWizard() {
        setWizardTarget(null);
        loadPools();
        loadDisks();
    }

    // Teardown and protect-tier both change which disks are blank/in-use, same as a
    // build/expand does -- but unlike those (handled via closeWizard above), they're triggered
    // inline from PoolsTable rather than through the wizard's close callback, so they need their
    // own combined refresh rather than reusing the manual "Refresh" button's pools-only reload
    // (kept pools-only deliberately -- see docs/cockpit-plugin.md bug #6 -- since there's already
    // a separate dedicated disk-refresh button on the Disk inventory tab).
    function refreshPoolsAndDisks() {
        loadPools();
        loadDisks();
    }

    // Mirrors the daemon's own DiskSelector.EnsureBlank gate (plus TieringPlanner's 2-disk
    // minimum for a brand-new pool) so Create pool/Expand can be disabled with an explanatory
    // hint up front, instead of only failing after the wizard is already open.
    const availableDiskCount = countAvailableDisks(disks, pools);
    const loadingInventory = poolsLoading || disksLoading;
    const createDisabledReason = loadingInventory
        ? null
        : availableDiskCount < 2
            ? `Need at least 2 blank, unused disks to create a pool (${availableDiskCount} available). `
                + "Wipe or attach more disks first."
            : null;
    const expandDisabledReason = loadingInventory
        ? null
        : availableDiskCount < 1
            ? "No blank, unused disks available to expand with. Wipe or attach a disk first."
            : null;

    // sidebar={null} is required, not cosmetic: Page only applies its
    // pf-m-no-sidebar modifier when sidebar === null, not merely absent, so
    // without it Page still reserves a "sidebar" grid column at >=75rem
    // viewport width that nothing occupies -- an empty column pushing all
    // real content right by its width.
    return (
        <Page sidebar={null}>
            <PageSection>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                    <Title headingLevel="h1" style={{ marginBottom: "1rem" }}>DiskWeaver</Title>
                    <div style={{ display: "flex", gap: "8px", alignItems: "center" }}>
                        <ReassembleButton onDone={refreshPoolsAndDisks} />
                        {onLogout && <Button variant="link" onClick={onLogout}>Log out</Button>}
                    </div>
                </div>
                <ErrorBanner message={error} />
                <Tabs activeKey={activeTabKey} onSelect={(_, key) => setActiveTabKey(key)}>
                    <Tab eventKey="pools" title={<TabTitleText>Pools</TabTitleText>}>
                        <PageSection>
                            <PoolsTable
                                pools={pools}
                                disks={disks}
                                loading={poolsLoading}
                                onRefresh={loadPools}
                                onDataChanged={refreshPoolsAndDisks}
                                onExpand={poolName => setWizardTarget(poolName)}
                                onCreate={() => setWizardTarget("")}
                                expandDisabledReason={expandDisabledReason}
                                createDisabledReason={createDisabledReason}
                            />
                        </PageSection>
                    </Tab>
                    <Tab eventKey="inventory" title={<TabTitleText>Disk inventory</TabTitleText>}>
                        <PageSection>
                            <DiskInventory disks={disks} pools={pools} loading={disksLoading} onRefresh={loadDisks} />
                        </PageSection>
                    </Tab>
                </Tabs>

                {wizardTarget !== null && (
                    <CreateExpandWizard
                        pools={pools}
                        disks={disks}
                        expansionPoolName={wizardTarget || null}
                        onClose={closeWizard}
                    />
                )}
            </PageSection>
        </Page>
    );
}
