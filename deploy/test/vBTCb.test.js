const { expect } = require("chai");
const { ethers, upgrades, artifacts } = require("hardhat");
const { time, loadFixture } = require("@nomicfoundation/hardhat-network-helpers");
const fs = require("fs");
const path = require("path");

const HOUR = 3600;
const DAY = 24 * HOUR;

const TYPES = {
    Mint: [
        { name: "to", type: "address" },
        { name: "amount", type: "uint256" },
        { name: "lockId", type: "string" },
        { name: "nonce", type: "uint256" },
    ],
    AddValidators: [
        { name: "validators", type: "address[]" },
        { name: "vfxBlockHeight", type: "uint256" },
        { name: "adminNonce", type: "uint256" },
    ],
    RemoveValidators: [
        { name: "validators", type: "address[]" },
        { name: "vfxBlockHeight", type: "uint256" },
        { name: "adminNonce", type: "uint256" },
    ],
    ProposeUpgrade: [
        { name: "implementation", type: "address" },
        { name: "adminNonce", type: "uint256" },
    ],
    ProposeUpgradeDelay: [
        { name: "delay", type: "uint256" },
        { name: "adminNonce", type: "uint256" },
    ],
    ProposeRemoval: [
        { name: "validators", type: "address[]" },
        { name: "casters", type: "address[]" },
        { name: "vfxBlockHeight", type: "uint256" },
        { name: "adminNonce", type: "uint256" },
    ],
    CancelPending: [{ name: "adminNonce", type: "uint256" }],
    UpdateCasters: [
        { name: "add", type: "address[]" },
        { name: "remove", type: "address[]" },
        { name: "vfxBlockHeight", type: "uint256" },
        { name: "adminNonce", type: "uint256" },
    ],
};

async function domainOf(token) {
    const { chainId } = await ethers.provider.getNetwork();
    return { name: "vBTCb", version: "4", chainId, verifyingContract: await token.getAddress() };
}

async function signTyped(token, signers, typeName, value, domainOverride) {
    const domain = domainOverride ?? (await domainOf(token));
    return Promise.all(signers.map((w) => w.signTypedData(domain, { [typeName]: TYPES[typeName] }, value)));
}

function wallets(n) {
    return Array.from({ length: n }, () => ethers.Wallet.createRandom());
}

// The V3 ADD message the audit found: its encodePacked bytes are also valid mintWithProof arguments.
// The VFX height 0x414243 makes the 3 bytes that land in lockId the ASCII string "ABC".
function v3AddCollision(newValidator, adminNonce, chainId, contract) {
    const vfxBlockHeight = 0x414243n;
    const packed = ethers.solidityPacked(
        ["string", "address", "uint256", "uint256", "uint256", "address"],
        ["ADD", newValidator, vfxBlockHeight, adminNonce, chainId, contract]
    );
    const b = ethers.getBytes(packed);
    return {
        addHash: ethers.keccak256(packed),
        mint: {
            to: ethers.getAddress(ethers.hexlify(b.slice(0, 20))),
            amount: BigInt(ethers.hexlify(b.slice(20, 52))),
            lockId: ethers.toUtf8String(b.slice(52, 55)),
            nonce: BigInt(ethers.hexlify(b.slice(55, 87))),
        },
    };
}

async function deployV4Fixture() {
    const vals = wallets(3);
    const [payer, user] = await ethers.getSigners();
    const V4 = await ethers.getContractFactory("vBTCb");
    // The same three keys sit on both lists, as the seed casters do on mainnet.
    const token = await upgrades.deployProxy(
        V4,
        ["Verified Bitcoin on Base", "vBTC.b", vals.map((v) => v.address), vals.map((v) => v.address)],
        { initializer: "initialize", kind: "uups" }
    );
    await token.waitForDeployment();
    return { token, vals, payer, user, V4 };
}

// Three validators and three casters, no key on both.
async function deploySplitFixture() {
    const vals = wallets(3);
    const casters = wallets(3);
    const [, user] = await ethers.getSigners();
    const V4 = await ethers.getContractFactory("vBTCb");
    const token = await upgrades.deployProxy(V4, ["n", "s", vals.map((v) => v.address), casters.map((c) => c.address)], {
        kind: "uups",
    });
    return { token, vals, casters, user };
}

async function updateCasters(token, signers, added, removed = [], height = 100n) {
    const adminNonce = await token.adminNonce();
    const add = added.map((a) => a.address);
    const value = { add, remove: removed, vfxBlockHeight: height, adminNonce };
    const sigs = await signTyped(token, signers, "UpdateCasters", value);
    const own = await Promise.all(added.map((a) => signTyped(token, [a], "UpdateCasters", value).then((s) => s[0])));
    return token.updateCasters(add, removed, height, sigs, own);
}

async function mint(token, signers, to, amount, lockId, nonce = 1n) {
    const sigs = await signTyped(token, signers, "Mint", { to, amount, lockId, nonce });
    return token.mintWithProof(to, amount, lockId, nonce, sigs);
}

// `added` are the new validators' wallets (each signs its own addition) or plain addresses (no such signature).
async function addBatch(token, signers, added, height = 100n) {
    const adminNonce = await token.adminNonce();
    const addrs = added.map((a) => (typeof a === "string" ? a : a.address));
    const value = { validators: addrs, vfxBlockHeight: height, adminNonce };
    const sigs = await signTyped(token, signers, "AddValidators", value);
    const own = await Promise.all(
        added.map((a) => (typeof a === "string" ? "0x" : signTyped(token, [a], "AddValidators", value).then((s) => s[0])))
    );
    return token.addValidatorBatch(addrs, height, sigs, own);
}

async function removeBatch(token, signers, addrs, height = 100n) {
    const adminNonce = await token.adminNonce();
    const sigs = await signTyped(token, signers, "RemoveValidators", { validators: addrs, vfxBlockHeight: height, adminNonce });
    return token.removeValidatorBatch(addrs, height, sigs);
}

async function newImplementation() {
    const V4 = await ethers.getContractFactory("vBTCb");
    const impl = await V4.deploy();
    await impl.waitForDeployment();
    return impl.getAddress();
}

describe("vBTCb V4", function () {
    describe("initialize", function () {
        it("seeds 3 validators with 2-of-3 mint and remove thresholds and a 48 h upgrade delay", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            expect(await token.getValidators()).to.deep.equal(vals.map((v) => v.address));
            expect(await token.requiredMintSignatures()).to.equal(2n);
            expect(await token.requiredRemoveSignatures()).to.equal(2n);
            expect(await token.upgradeDelay()).to.equal(BigInt(48 * HOUR));
            expect(await token.decimals()).to.equal(8n);
        });

        it("refuses fewer than 3 validators", async function () {
            const V4 = await ethers.getContractFactory("vBTCb");
            await expect(
                upgrades.deployProxy(V4, ["n", "s", wallets(2).map((v) => v.address), wallets(3).map((v) => v.address)], { kind: "uups" })
            ).to.be.revertedWith("Need at least 3 validators");
        });

        it("refuses more than 50 validators", async function () {
            const V4 = await ethers.getContractFactory("vBTCb");
            await expect(
                upgrades.deployProxy(V4, ["n", "s", wallets(51).map((v) => v.address), wallets(3).map((v) => v.address)], { kind: "uups" })
            ).to.be.revertedWith("Too many validators");
        });

        it("cannot be initialized on the implementation itself", async function () {
            const V4 = await ethers.getContractFactory("vBTCb");
            const impl = await V4.deploy();
            await expect(impl.initialize("n", "s", wallets(3).map((v) => v.address), wallets(3).map((v) => v.address))).to.be.revertedWithCustomError(
                impl,
                "InvalidInitialization"
            );
        });
    });

    describe("mintWithProof", function () {
        it("mints with 2 of 3 validator signatures and refuses the same lockId twice", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            await expect(mint(token, vals.slice(0, 2), user.address, 10_000n, "lock-1"))
                .to.emit(token, "MintExecuted")
                .withArgs(user.address, 10_000n, "lock-1", 1n);
            expect(await token.balanceOf(user.address)).to.equal(10_000n);
            expect(await token.isLockIdUsed("lock-1")).to.equal(true);
            await expect(mint(token, vals.slice(1, 3), user.address, 10_000n, "lock-1")).to.be.revertedWith(
                "LockId already used"
            );
        });

        it("refuses one signature, the same signer twice, and non-validator signatures", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const value = { to: user.address, amount: 1n, lockId: "l", nonce: 1n };
            const [s0] = await signTyped(token, [vals[0]], "Mint", value);
            const outsiders = await signTyped(token, wallets(5), "Mint", value);
            await expect(token.mintWithProof(user.address, 1n, "l", 1n, [s0])).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await expect(token.mintWithProof(user.address, 1n, "l", 1n, [s0, s0])).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await expect(token.mintWithProof(user.address, 1n, "l", 1n, [s0, ...outsiders])).to.be.revertedWith(
                "Insufficient valid signatures"
            );
        });

        it("skips a malformed signature instead of reverting the mint", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const value = { to: user.address, amount: 5n, lockId: "m", nonce: 1n };
            const good = await signTyped(token, vals.slice(0, 2), "Mint", value);
            const garbage = ["0x", "0x1234", ethers.hexlify(ethers.randomBytes(65))];
            await token.mintWithProof(user.address, 5n, "m", 1n, [...garbage, ...good]);
            expect(await token.balanceOf(user.address)).to.equal(5n);
        });

        it("refuses signatures made for another contract or another chain", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const value = { to: user.address, amount: 1n, lockId: "x", nonce: 1n };
            const base = await domainOf(token);
            const otherContract = await signTyped(token, vals, "Mint", value, {
                ...base,
                verifyingContract: ethers.Wallet.createRandom().address,
            });
            const otherChain = await signTyped(token, vals, "Mint", value, { ...base, chainId: 8453n });
            await expect(token.mintWithProof(user.address, 1n, "x", 1n, otherContract)).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await expect(token.mintWithProof(user.address, 1n, "x", 1n, otherChain)).to.be.revertedWith(
                "Insufficient valid signatures"
            );
        });

        it("refuses signatures over the old V3 mint encoding", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const { chainId } = await ethers.provider.getNetwork();
            const v3Hash = ethers.solidityPackedKeccak256(
                ["address", "uint256", "string", "uint256", "uint256", "address"],
                [user.address, 1n, "v3", 1n, chainId, await token.getAddress()]
            );
            const sigs = await Promise.all(vals.map((v) => v.signMessage(ethers.getBytes(v3Hash))));
            await expect(token.mintWithProof(user.address, 1n, "v3", 1n, sigs)).to.be.revertedWith(
                "Insufficient valid signatures"
            );
        });
    });

    describe("signatures are bound to one message type", function () {
        it("the audit's V3 ADD-as-mint input mints nothing on V4", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const { chainId } = await ethers.provider.getNetwork();
            const c = v3AddCollision(ethers.Wallet.createRandom().address, 0n, chainId, await token.getAddress());
            const sigs = await Promise.all(vals.map((v) => v.signMessage(ethers.getBytes(c.addHash))));
            await expect(token.mintWithProof(c.mint.to, c.mint.amount, c.mint.lockId, c.mint.nonce, sigs)).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            expect(await token.totalSupply()).to.equal(0n);
        });

        it("AddValidators signatures do not validate as RemoveValidators, and the reverse", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const extraW = wallets(1);
            const extra = extraW.map((w) => w.address);
            await addBatch(token, vals, extraW);
            const adminNonce = await token.adminNonce();
            const value = { validators: extra, vfxBlockHeight: 7n, adminNonce };
            const addSigs = await signTyped(token, vals, "AddValidators", value);
            await expect(token.removeValidatorBatch(extra, 7n, addSigs)).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            const fresh = wallets(1).map((w) => w.address);
            const removeSigs = await signTyped(token, vals, "RemoveValidators", { ...value, validators: fresh });
            await expect(token.addValidatorBatch(fresh, 7n, removeSigs, ["0x"])).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
        });

        it("ProposeUpgrade signatures cannot cancel, and CancelPending signatures cannot propose", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            const cancelSigs = await signTyped(token, vals, "CancelPending", { adminNonce: 0n });
            await expect(token.proposeUpgrade(impl, cancelSigs)).to.be.revertedWith("Insufficient valid admin signatures");

            await token.proposeUpgrade(impl, await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 0n }));
            const proposeSigs = await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 1n });
            await expect(token.cancelPending(proposeSigs)).to.be.revertedWith("Insufficient valid admin signatures");
        });
    });

    describe("validator set", function () {
        it("adds a batch with 2 of 3, bumps the nonce and refuses a replay", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const addW = wallets(3);
            const add = addW.map((w) => w.address);
            const value = { validators: add, vfxBlockHeight: 100n, adminNonce: 0n };
            const sigs = await signTyped(token, vals.slice(0, 2), "AddValidators", value);
            const own = await signTyped(token, addW, "AddValidators", value);
            await expect(token.addValidatorBatch(add, 100n, sigs, own)).to.emit(token, "ValidatorBatchAdded");
            expect(await token.validatorCount()).to.equal(6n);
            expect(await token.adminNonce()).to.equal(1n);
            expect(await token.requiredMintSignatures()).to.equal(4n);
            expect(await token.requiredRemoveSignatures()).to.equal(4n);
            await expect(token.addValidatorBatch(add, 100n, sigs, own)).to.be.revertedWith("Insufficient valid admin signatures");
        });

        it("adds only an address that signed its own addition", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const newcomer = ethers.Wallet.createRandom();
            const value = { validators: [newcomer.address], vfxBlockHeight: 100n, adminNonce: 0n };
            const sigs = await signTyped(token, vals, "AddValidators", value);
            const [byOther] = await signTyped(token, [ethers.Wallet.createRandom()], "AddValidators", value);
            const [ownOtherMessage] = await signTyped(token, [newcomer], "AddValidators", { ...value, vfxBlockHeight: 101n });
            for (const own of ["0x", byOther, ownOtherMessage, ethers.hexlify(ethers.randomBytes(65))]) {
                await expect(token.addValidatorBatch([newcomer.address], 100n, sigs, [own])).to.be.revertedWith(
                    "New validator did not sign"
                );
            }
            await expect(token.addValidatorBatch([newcomer.address], 100n, sigs, [])).to.be.revertedWith(
                "One signature per new validator"
            );
            const [own] = await signTyped(token, [newcomer], "AddValidators", value);
            await token.addValidatorBatch([newcomer.address], 100n, sigs, [own]);
            expect(await token.isValidator(newcomer.address)).to.equal(true);
        });

        it("refuses one signature, a repeated address in a batch, and an existing validator", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const a = ethers.Wallet.createRandom();
            await expect(addBatch(token, vals.slice(0, 1), [a])).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(addBatch(token, vals, [a, a])).to.be.revertedWith("Already a validator");
            await expect(addBatch(token, vals, [vals[0]])).to.be.revertedWith("Already a validator");
            await expect(addBatch(token, vals, [ethers.ZeroAddress])).to.be.revertedWith("Invalid address");
        });

        it("never holds more than 50 validators", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            await addBatch(token, vals, wallets(47));
            expect(await token.validatorCount()).to.equal(50n);
            const all = vals; // the original 3 alone are not 2/3 of 50, but the cap check runs first
            await expect(addBatch(token, all, [ethers.Wallet.createRandom().address])).to.be.revertedWith(
                "Too many validators"
            );
        });

        it("never drops below 3 validators", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            await expect(removeBatch(token, vals, [vals[2].address])).to.be.revertedWith("Cannot remove below minimum");
            const four = wallets(1);
            await addBatch(token, vals, [four[0]]);
            const set = [...vals, ...four];
            await expect(removeBatch(token, set, [vals[2].address, four[0].address])).to.be.revertedWith(
                "Cannot remove below minimum"
            );
            await expect(removeBatch(token, set, [vals[2].address, vals[2].address])).to.be.revertedWith(
                "Cannot remove below minimum"
            );
        });

        it("removes with the mint threshold, and a removed validator's signature stops counting", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const extra = wallets(1);
            await addBatch(token, vals, [extra[0]]);
            // 4 validators: mint needs 3, and so does an immediate removal
            expect(await token.requiredMintSignatures()).to.equal(3n);
            await expect(removeBatch(token, [vals[0], vals[1]], [vals[2].address])).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            await expect(removeBatch(token, [vals[0], vals[1], extra[0]], [vals[2].address])).to.emit(token, "ValidatorBatchRemoved");
            expect(await token.isValidator(vals[2].address)).to.equal(false);
            expect(await token.validatorCount()).to.equal(3n);
            expect(await token.requiredMintSignatures()).to.equal(2n);
            await expect(mint(token, [vals[2], vals[0]], user.address, 1n, "after-remove")).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await mint(token, [extra[0], vals[0], vals[1]], user.address, 1n, "after-remove"); // vals[0..1]: the caster majority
        });
    });

    describe("a group short of 2/3 cannot take the set over", function () {
        // 7 validators: mint and immediate removal 5, a removal proposal 4 (majority), a veto 3 (blocking minority).
        async function deploy7Fixture() {
            const vals = wallets(7);
            const [, user] = await ethers.getSigners();
            const V4 = await ethers.getContractFactory("vBTCb");
            // The first three are also the casters: every group named below holds or lacks a caster majority as it says.
            const token = await upgrades.deployProxy(V4, ["n", "s", vals.map((v) => v.address), vals.slice(0, 3).map((v) => v.address)], { kind: "uups" });
            return { token, vals, user, majority: vals.slice(0, 4), others: vals.slice(4) };
        }

        async function proposeRemoval(token, signers, addrs, height = 9n) {
            const adminNonce = await token.adminNonce();
            const sigs = await signTyped(token, signers, "ProposeRemoval", { validators: addrs, casters: [], vfxBlockHeight: height, adminNonce });
            return token.proposeRemoval(addrs, [], height, sigs);
        }

        async function cancel(token, signers) {
            const sigs = await signTyped(token, signers, "CancelPending", { adminNonce: await token.adminNonce() });
            return token.cancelPending(sigs);
        }

        it("has the thresholds 5 / 4 / 3 at 7 validators and keeps veto = n - mint + 1 at every size", async function () {
            const { token } = await loadFixture(deploy7Fixture);
            expect(await token.requiredMintSignatures()).to.equal(5n);
            expect(await token.requiredRemoveSignatures()).to.equal(4n);
            expect(await token.requiredVetoSignatures()).to.equal(3n);
            const V4 = await ethers.getContractFactory("vBTCb");
            for (const n of [3, 4, 5, 6, 10, 49, 50]) {
                const t = await upgrades.deployProxy(V4, ["n", "s", wallets(n).map((v) => v.address), wallets(3).map((v) => v.address)], { kind: "uups" });
                const mint = await t.requiredMintSignatures();
                const majority = await t.requiredRemoveSignatures();
                expect(mint * 3n >= BigInt(n) * 2n, `n=${n}`).to.equal(true);
                expect(majority * 2n > BigInt(n), `n=${n}`).to.equal(true);
                expect(await t.requiredVetoSignatures()).to.equal(BigInt(n) - mint + 1n);
            }
        });

        it("a majority cannot remove the others at once and then mint", async function () {
            const { token, majority, others, user } = await loadFixture(deploy7Fixture);
            await expect(removeBatch(token, majority, others.map((o) => o.address))).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            await expect(mint(token, majority, user.address, 1n, "x")).to.be.revertedWith("Insufficient valid signatures");
        });

        it("a majority's removal waits the delay, and the minority it targets vetoes it", async function () {
            const { token, majority, others } = await loadFixture(deploy7Fixture);
            const targets = others.map((o) => o.address);
            await expect(proposeRemoval(token, majority.slice(0, 3), targets)).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            await expect(proposeRemoval(token, majority, targets)).to.emit(token, "RemovalProposed");
            expect(await token.getPendingRemovals()).to.deep.equal(targets);
            await expect(token.executeRemoval()).to.be.revertedWith("Upgrade delay not elapsed");
            await expect(proposeRemoval(token, majority, targets)).to.be.revertedWith("Removal already pending");

            await expect(cancel(token, others.slice(0, 2))).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(cancel(token, others)).to.emit(token, "PendingCancelled");
            await time.increase(49 * HOUR);
            await expect(token.executeRemoval()).to.be.revertedWith("No removal pending");
            expect(await token.validatorCount()).to.equal(7n);
        });

        it("a live majority clears validators that went dark, after the delay, and can then mint", async function () {
            const { token, majority, others, user } = await loadFixture(deploy7Fixture);
            await proposeRemoval(token, majority, others.map((o) => o.address));
            await time.increase(48 * HOUR);
            await expect(token.connect(user).executeRemoval()).to.emit(token, "ValidatorBatchRemoved");
            expect(await token.validatorCount()).to.equal(4n);
            expect(await token.requiredMintSignatures()).to.equal(3n);
            expect(await token.getPendingRemovals()).to.deep.equal([]);
            await mint(token, majority.slice(0, 3), user.address, 7n, "after-recovery");
            expect(await token.balanceOf(user.address)).to.equal(7n);
        });

        it("a pending removal skips a validator the 2/3 path already removed", async function () {
            const { token, vals, majority, others } = await loadFixture(deploy7Fixture);
            await proposeRemoval(token, majority, [others[0].address, others[1].address]);
            await removeBatch(token, vals.slice(0, 5), [others[0].address]);
            await time.increase(48 * HOUR);
            await token.executeRemoval();
            expect(await token.validatorCount()).to.equal(5n);
            expect(await token.isValidator(others[1].address)).to.equal(false);
        });

        it("a removal proposal refuses repeats, non-validators and going below 3", async function () {
            const { token, vals, majority, others } = await loadFixture(deploy7Fixture);
            await expect(proposeRemoval(token, majority, [others[0].address, others[0].address])).to.be.revertedWith(
                "Duplicate in batch"
            );
            await expect(proposeRemoval(token, majority, [ethers.Wallet.createRandom().address])).to.be.revertedWith(
                "Not a validator"
            );
            await expect(proposeRemoval(token, majority, vals.slice(0, 5).map((v) => v.address))).to.be.revertedWith(
                "Cannot remove below minimum"
            );
        });
    });

    describe("two groups sign: 2/3 of the validators and a majority of the casters", function () {
        it("a mint needs both: either group alone, or one short, mints nothing", async function () {
            const { token, vals, casters, user } = await loadFixture(deploySplitFixture);
            expect(await token.requiredMintSignatures()).to.equal(2n);
            expect(await token.requiredCasterSignatures()).to.equal(2n);
            await expect(mint(token, vals, user.address, 1n, "v")).to.be.revertedWith("Insufficient valid signatures");
            await expect(mint(token, casters, user.address, 1n, "c")).to.be.revertedWith("Insufficient valid signatures");
            await expect(mint(token, [...vals.slice(0, 2), casters[0]], user.address, 1n, "vc")).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await mint(token, [vals[0], casters[1], vals[2], casters[2]], user.address, 1n, "both");
            expect(await token.balanceOf(user.address)).to.equal(1n);
        });

        it("however many validator seats one party holds, without the casters it mints and changes nothing", async function () {
            const { token, vals, casters, user } = await loadFixture(deploySplitFixture);
            const sybils = wallets(47);
            await addBatch(token, [...vals, ...casters.slice(0, 2)], sybils); // 50 validators, 47 of them one party's
            expect(await token.validatorCount()).to.equal(50n);
            const impl = await newImplementation();
            const nonce = await token.adminNonce();

            await expect(mint(token, sybils, user.address, 1_000_000n, "unbacked")).to.be.revertedWith("Insufficient valid signatures");
            await expect(removeBatch(token, sybils, vals.map((v) => v.address))).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(token.proposeUpgrade(impl, await signTyped(token, sybils, "ProposeUpgrade", { implementation: impl, adminNonce: nonce })))
                .to.be.revertedWith("Insufficient valid admin signatures");
            const own = wallets(3);
            await expect(updateCasters(token, sybils, own, casters.map((c) => c.address))).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            expect(await token.totalSupply()).to.equal(0n);
        });

        it("the casters alone change nothing either", async function () {
            const { token, casters } = await loadFixture(deploySplitFixture);
            await expect(addBatch(token, casters, wallets(1))).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(updateCasters(token, casters, wallets(1))).to.be.revertedWith("Insufficient valid admin signatures");
        });

        it("updateCasters swaps a caster with both groups, needs the new caster's own signature, and keeps 3 to 10", async function () {
            const { token, vals, casters } = await loadFixture(deploySplitFixture);
            const both = [...vals.slice(0, 2), ...casters.slice(0, 2)];
            const fresh = ethers.Wallet.createRandom();
            const value = { add: [fresh.address], remove: [casters[2].address], vfxBlockHeight: 100n, adminNonce: 0n };
            const sigs = await signTyped(token, both, "UpdateCasters", value);
            const [byOther] = await signTyped(token, [ethers.Wallet.createRandom()], "UpdateCasters", value);
            await expect(token.updateCasters(value.add, value.remove, 100n, sigs, [byOther])).to.be.revertedWith("New caster did not sign");
            await expect(token.updateCasters(value.add, value.remove, 100n, sigs, [])).to.be.revertedWith("One signature per new caster");
            const [own] = await signTyped(token, [fresh], "UpdateCasters", value);
            await expect(token.updateCasters(value.add, value.remove, 100n, sigs, [own])).to.emit(token, "CastersUpdated");
            expect(await token.isCaster(casters[2].address)).to.equal(false);
            expect(await token.isCaster(fresh.address)).to.equal(true);
            expect(await token.adminNonce()).to.equal(1n);
            await expect(token.updateCasters(value.add, value.remove, 100n, sigs, [own])).to.be.revertedWith("Insufficient valid admin signatures");

            const now = [casters[0], casters[1], fresh];
            await expect(updateCasters(token, [...vals, ...now], [], [fresh.address])).to.be.revertedWith("Caster count out of range");
            const sevenMore = wallets(7);
            await updateCasters(token, [...vals, ...now], sevenMore);
            expect(await token.casterCount()).to.equal(10n);
            await expect(updateCasters(token, [...vals, ...now, ...sevenMore], wallets(1))).to.be.revertedWith("Caster count out of range");
        });

        it("a removed caster's signature stops counting", async function () {
            const { token, vals, casters, user } = await loadFixture(deploySplitFixture);
            const fresh = wallets(1);
            await updateCasters(token, [...vals, ...casters], fresh, [casters[2].address]);
            await expect(mint(token, [...vals, casters[2], casters[0]], user.address, 1n, "x")).to.be.revertedWith(
                "Insufficient valid signatures"
            );
            await mint(token, [...vals, fresh[0], casters[0]], user.address, 1n, "x");
        });

        async function proposeCasterRemoval(token, signers, casterAddrs, validatorAddrs = []) {
            const adminNonce = await token.adminNonce();
            const value = { validators: validatorAddrs, casters: casterAddrs, vfxBlockHeight: 9n, adminNonce };
            return token.proposeRemoval(validatorAddrs, casterAddrs, 9n, await signTyped(token, signers, "ProposeRemoval", value));
        }

        it("dark casters are cleared by 2/3 of the validators after the delay, and a caster blocking minority can stop it", async function () {
            const { token, vals, casters, user } = await loadFixture(deploySplitFixture);
            const replacements = wallets(3);
            await updateCasters(token, [...vals, ...casters], replacements); // 6 casters: majority 4, blocking minority 3
            const gone = casters.map((c) => c.address);

            // A majority of the validators is not enough alone; 2/3 is, and it waits the delay.
            const more = wallets(4);
            await addBatch(token, [...vals, ...casters, replacements[0]], more); // 7 validators: majority 4, 2/3 5
            const seven = [...vals, ...more];
            await expect(proposeCasterRemoval(token, seven.slice(0, 4), gone)).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(proposeCasterRemoval(token, seven.slice(0, 5), gone)).to.emit(token, "RemovalProposed");
            expect(await token.getPendingCasterRemovals()).to.deep.equal(gone);
            await expect(token.executeRemoval()).to.be.revertedWith("Upgrade delay not elapsed");

            // Casters who are not dark cancel it: three of six is their blocking minority.
            const cancelSigs = async (signers) => signTyped(token, signers, "CancelPending", { adminNonce: await token.adminNonce() });
            await expect(token.cancelPending(await cancelSigs(replacements.slice(0, 2)))).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(token.cancelPending(await cancelSigs(replacements))).to.emit(token, "PendingCancelled");

            // Truly dark: nobody cancels, and after the delay the remaining casters carry on.
            await proposeCasterRemoval(token, seven.slice(0, 5), gone);
            await time.increase(48 * HOUR);
            await expect(token.connect(user).executeRemoval()).to.emit(token, "CastersUpdated");
            expect([...(await token.getCasters())].sort()).to.deep.equal(replacements.map((r) => r.address).sort());
            expect(await token.requiredCasterSignatures()).to.equal(2n);
            await mint(token, [...seven.slice(0, 5), ...replacements.slice(0, 2)], user.address, 3n, "after-clearing");
        });

        it("a caster removal refuses going below 3 casters, a non-caster and a repeat", async function () {
            const { token, vals, casters } = await loadFixture(deploySplitFixture);
            await expect(proposeCasterRemoval(token, vals, [casters[0].address])).to.be.revertedWith("Cannot remove casters below minimum");
            await updateCasters(token, [...vals, ...casters], wallets(2));
            await expect(proposeCasterRemoval(token, vals, [vals[0].address])).to.be.revertedWith("Not a caster");
            await expect(proposeCasterRemoval(token, vals, [casters[0].address, casters[0].address])).to.be.revertedWith("Duplicate in batch");
        });

        it("a caster blocking minority cancels a pending upgrade", async function () {
            const { token, vals, casters } = await loadFixture(deploySplitFixture);
            const impl = await newImplementation();
            await token.proposeUpgrade(impl, await signTyped(token, [...vals, ...casters], "ProposeUpgrade", { implementation: impl, adminNonce: 0n }));
            await expect(token.cancelPending(await signTyped(token, casters.slice(0, 1), "CancelPending", { adminNonce: 1n }))).to.be.revertedWith(
                "Insufficient valid admin signatures"
            );
            await token.cancelPending(await signTyped(token, casters.slice(1), "CancelPending", { adminNonce: 1n }));
            expect(await token.pendingImplementation()).to.equal(ethers.ZeroAddress);
        });

        it("initialize refuses fewer than 3 or more than 10 casters and a repeated caster", async function () {
            const V4 = await ethers.getContractFactory("vBTCb");
            const v = wallets(3).map((w) => w.address);
            for (const c of [wallets(2).map((w) => w.address), wallets(11).map((w) => w.address)])
                await expect(upgrades.deployProxy(V4, ["n", "s", v, c], { kind: "uups" })).to.be.revertedWith("Caster count out of range");
            const one = ethers.Wallet.createRandom().address;
            await expect(upgrades.deployProxy(V4, ["n", "s", v, [one, one, v[0]]], { kind: "uups" })).to.be.revertedWith("Duplicate caster");
        });
    });

    describe("upgrades", function () {
        it("cannot be upgraded directly through upgradeToAndCall", async function () {
            const { token } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            await expect(token.upgradeToAndCall(impl, "0x")).to.be.revertedWith(
                "vBTCb: use proposeUpgrade and executeUpgrade"
            );
        });

        it("waits 48 hours between proposal and execution, then anyone can execute", async function () {
            const { token, vals, user } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            const sigs = await signTyped(token, vals.slice(0, 2), "ProposeUpgrade", { implementation: impl, adminNonce: 0n });
            await expect(token.proposeUpgrade(impl, sigs)).to.emit(token, "UpgradeProposed");
            const eta = await token.pendingUpgradeEta();
            expect(eta - BigInt(await time.latest())).to.equal(BigInt(48 * HOUR));
            expect(await token.adminNonce()).to.equal(1n);

            await expect(token.connect(user).executeUpgrade()).to.be.revertedWith("Upgrade delay not elapsed");
            await time.increaseTo(eta - 2n);
            await expect(token.connect(user).executeUpgrade()).to.be.revertedWith("Upgrade delay not elapsed");
            await time.increaseTo(eta);
            await expect(token.connect(user).executeUpgrade()).to.emit(token, "ContractUpgraded").withArgs(impl, 1n);
            expect(await upgrades.erc1967.getImplementationAddress(await token.getAddress())).to.equal(impl);
            expect(await token.pendingImplementation()).to.equal(ethers.ZeroAddress);
            await expect(token.executeUpgrade()).to.be.revertedWith("No upgrade pending");
        });

        it("refuses a proposal with one signature, for a non-contract, or while one is pending", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            const one = await signTyped(token, vals.slice(0, 1), "ProposeUpgrade", { implementation: impl, adminNonce: 0n });
            await expect(token.proposeUpgrade(impl, one)).to.be.revertedWith("Insufficient valid admin signatures");
            const eoa = ethers.Wallet.createRandom().address;
            const eoaSigs = await signTyped(token, vals, "ProposeUpgrade", { implementation: eoa, adminNonce: 0n });
            await expect(token.proposeUpgrade(eoa, eoaSigs)).to.be.revertedWith("Invalid implementation");

            const sigs = await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 0n });
            await token.proposeUpgrade(impl, sigs);
            const impl2 = await newImplementation();
            const sigs2 = await signTyped(token, vals, "ProposeUpgrade", { implementation: impl2, adminNonce: 1n });
            await expect(token.proposeUpgrade(impl2, sigs2)).to.be.revertedWith("Upgrade already pending");
        });

        it("a majority cancels a pending upgrade, which then cannot execute", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            const before = await upgrades.erc1967.getImplementationAddress(await token.getAddress());
            const sigs = await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 0n });
            await token.proposeUpgrade(impl, sigs);
            const cancel = await signTyped(token, vals.slice(1, 3), "CancelPending", { adminNonce: 1n });
            await expect(token.cancelPending(cancel)).to.emit(token, "PendingCancelled");
            await time.increase(49 * HOUR);
            await expect(token.executeUpgrade()).to.be.revertedWith("No upgrade pending");
            expect(await upgrades.erc1967.getImplementationAddress(await token.getAddress())).to.equal(before);
            await expect(token.cancelPending(cancel)).to.be.revertedWith("Nothing pending");
        });

        it("the delay can be raised, the change waits the current delay, and later proposals use the new delay", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const sevenDays = BigInt(7 * DAY);
            const sigs = await signTyped(token, vals, "ProposeUpgradeDelay", { delay: sevenDays, adminNonce: 0n });
            await token.proposeUpgradeDelay(sevenDays, sigs);
            await expect(token.executeUpgradeDelay()).to.be.revertedWith("Upgrade delay not elapsed");
            await time.increaseTo(await token.pendingUpgradeDelayEta());
            await expect(token.executeUpgradeDelay()).to.emit(token, "UpgradeDelayChanged").withArgs(sevenDays);
            expect(await token.upgradeDelay()).to.equal(sevenDays);

            // Going back down to 48 h now takes the 7-day delay.
            const back = await signTyped(token, vals, "ProposeUpgradeDelay", { delay: BigInt(48 * HOUR), adminNonce: 1n });
            await token.proposeUpgradeDelay(BigInt(48 * HOUR), back);
            expect((await token.pendingUpgradeDelayEta()) - BigInt(await time.latest())).to.equal(sevenDays);

            const impl = await newImplementation();
            const up = await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 2n });
            await token.proposeUpgrade(impl, up);
            expect((await token.pendingUpgradeEta()) - BigInt(await time.latest())).to.equal(sevenDays);
        });

        it("an upgrade, a delay change and a removal lapse 14 days after they became executable, and can then be replaced", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const extra = wallets(1);
            await addBatch(token, vals, extra); // 4 validators, so one can be removed; nonce 1
            const set = [...vals, ...extra];
            const impl = await newImplementation();
            await token.proposeUpgrade(impl, await signTyped(token, set, "ProposeUpgrade", { implementation: impl, adminNonce: 1n }));
            const delay = BigInt(7 * DAY);
            await token.proposeUpgradeDelay(delay, await signTyped(token, set, "ProposeUpgradeDelay", { delay, adminNonce: 2n }));
            const removal = { validators: [extra[0].address], casters: [], vfxBlockHeight: 9n, adminNonce: 3n };
            await token.proposeRemoval(removal.validators, [], 9n, await signTyped(token, set, "ProposeRemoval", removal));

            const latest = await token.pendingRemovalEta(); // proposed last of the three
            await time.increaseTo(latest + BigInt(14 * DAY) + 1n); // one second past the last second it may run
            await expect(token.executeUpgrade()).to.be.revertedWith("Proposal lapsed");
            await expect(token.executeUpgradeDelay()).to.be.revertedWith("Proposal lapsed");
            await expect(token.executeRemoval()).to.be.revertedWith("Proposal lapsed");

            const impl2 = await newImplementation();
            await token.proposeUpgrade(impl2, await signTyped(token, set, "ProposeUpgrade", { implementation: impl2, adminNonce: 4n }));
            expect(await token.pendingImplementation()).to.equal(impl2);
            await token.proposeUpgradeDelay(delay, await signTyped(token, set, "ProposeUpgradeDelay", { delay, adminNonce: 5n }));
            await token.proposeRemoval(removal.validators, [], 9n, await signTyped(token, set, "ProposeRemoval", { ...removal, adminNonce: 6n }));
            await time.increaseTo(await token.pendingUpgradeEta());
            await token.executeUpgrade();
            await token.executeUpgradeDelay();
            await token.executeRemoval();
            expect(await upgrades.erc1967.getImplementationAddress(await token.getAddress())).to.equal(impl2);
            expect(await token.upgradeDelay()).to.equal(delay);
            expect(await token.validatorCount()).to.equal(3n);
        });

        it("the prepare script treats a lapsed proposal as nothing pending, as the contract does", async function () {
            const common = require("../scripts/upgrade-common");
            const { token, vals } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            await token.proposeUpgrade(impl, await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 0n }));
            const read = await common.contractAt(await token.getAddress());
            expect(await common.livePendingUpgrade(read)).to.equal(impl);
            await time.increaseTo((await token.pendingUpgradeEta()) + BigInt(14 * DAY));
            expect(await common.livePendingUpgrade(read)).to.equal(impl);             // its last second
            await time.increase(1);
            expect(await common.livePendingUpgrade(read)).to.equal(null);
        });

        it("a proposal still runs on the last second of its grace period", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            const impl = await newImplementation();
            await token.proposeUpgrade(impl, await signTyped(token, vals, "ProposeUpgrade", { implementation: impl, adminNonce: 0n }));
            await time.setNextBlockTimestamp((await token.pendingUpgradeEta()) + BigInt(14 * DAY));
            await token.executeUpgrade();
            expect(await upgrades.erc1967.getImplementationAddress(await token.getAddress())).to.equal(impl);
        });

        it("refuses a delay under 48 hours or over 30 days", async function () {
            const { token, vals } = await loadFixture(deployV4Fixture);
            for (const delay of [BigInt(47 * HOUR), BigInt(31 * DAY), 0n]) {
                const sigs = await signTyped(token, vals, "ProposeUpgradeDelay", { delay, adminNonce: 0n });
                await expect(token.proposeUpgradeDelay(delay, sigs)).to.be.revertedWith("Delay out of range");
            }
        });
    });

    describe("upgrade from the deployed V3", function () {
        async function deployV3Fixture() {
            const vals = wallets(3);
            const [payer, user] = await ethers.getSigners();
            const V3 = await ethers.getContractFactory("vBTCbV3");
            const token = await upgrades.deployProxy(V3, ["Verified Bitcoin on Base", "vBTC.b", vals.map((v) => v.address)], {
                initializer: "initialize",
                kind: "uups",
            });
            await token.waitForDeployment();
            const { chainId } = await ethers.provider.getNetwork();
            const self = await token.getAddress();
            const v3Mint = async (to, amount, lockId, nonce) => {
                const h = ethers.solidityPackedKeccak256(
                    ["address", "uint256", "string", "uint256", "uint256", "address"],
                    [to, amount, lockId, nonce, chainId, self]
                );
                const sigs = await Promise.all(vals.slice(0, 2).map((v) => v.signMessage(ethers.getBytes(h))));
                return token.mintWithProof(to, amount, lockId, nonce, sigs);
            };
            return { token, vals, user, chainId, self, v3Mint };
        }

        it("V3 accepts the ADD-as-mint input (the bug V4 removes)", async function () {
            const { token, vals, chainId, self } = await loadFixture(deployV3Fixture);
            const c = v3AddCollision(ethers.Wallet.createRandom().address, 0n, chainId, self);
            const sigs = await Promise.all(vals.slice(0, 2).map((v) => v.signMessage(ethers.getBytes(c.addHash))));
            await token.mintWithProof(c.mint.to, c.mint.amount, c.mint.lockId, c.mint.nonce, sigs);
            expect(await token.balanceOf(c.mint.to)).to.equal(c.mint.amount);
            expect(c.mint.to.toLowerCase().startsWith("0x414444")).to.equal(true);
        });

        it("upgrades through V3's upgradeWithValidatorApproval and keeps every balance, lock and validator", async function () {
            const { token, vals, user, chainId, self, v3Mint } = await loadFixture(deployV3Fixture);
            await v3Mint(user.address, 60_000n, "lock-a", 1n);
            await v3Mint(user.address, 30_000n, "lock-b", 2n);

            const V4 = await ethers.getContractFactory("vBTCb");
            const impl = await upgrades.prepareUpgrade(self, V4, { kind: "uups" }); // storage layout check
            const h = ethers.solidityPackedKeccak256(
                ["string", "address", "uint256", "uint256", "address"],
                ["UPGRADE", impl, 0n, chainId, self]
            );
            const sigs = await Promise.all(vals.slice(0, 2).map((v) => v.signMessage(ethers.getBytes(h))));
            await token.upgradeWithValidatorApproval(impl, sigs);

            const v4 = V4.attach(self);
            expect(await upgrades.erc1967.getImplementationAddress(self)).to.equal(impl);
            expect(await v4.balanceOf(user.address)).to.equal(90_000n);
            expect(await v4.totalSupply()).to.equal(90_000n);
            expect(await v4.name()).to.equal("Verified Bitcoin on Base");
            expect(await v4.symbol()).to.equal("vBTC.b");
            expect(await v4.getValidators()).to.deep.equal(vals.map((v) => v.address));
            expect(await v4.requiredMintSignatures()).to.equal(2n);
            expect(await v4.adminNonce()).to.equal(1n);
            expect(await v4.upgradeDelay()).to.equal(BigInt(48 * HOUR));
            expect(await v4.pendingImplementation()).to.equal(ethers.ZeroAddress);
            expect(await v4.isLockIdUsed("lock-a")).to.equal(true);
            expect(v4.interface.getFunction("upgradeWithValidatorApproval")).to.equal(null);

            await expect(mint(v4, vals.slice(0, 2), user.address, 1n, "lock-a")).to.be.revertedWith("LockId already used");
            await mint(v4, vals.slice(1, 3), user.address, 5_000n, "lock-c");
            expect(await v4.balanceOf(user.address)).to.equal(95_000n);
            await v4.connect(user).burnForVfxExit(5_000n, "RVfxDestination");
            expect(await v4.totalSupply()).to.equal(90_000n);

            const c = v3AddCollision(ethers.Wallet.createRandom().address, 1n, chainId, self);
            const collide = await Promise.all(vals.map((v) => v.signMessage(ethers.getBytes(c.addHash))));
            await expect(v4.mintWithProof(c.mint.to, c.mint.amount, c.mint.lockId, c.mint.nonce, collide)).to.be.revertedWith(
                "Insufficient valid signatures"
            );
        });

        it("has no casters after the upgrade until updateCasters adds them, and then needs them", async function () {
            const { token, vals, user, self } = await loadFixture(deployV3Fixture);
            const V4 = await ethers.getContractFactory("vBTCb");
            const impl = await upgrades.prepareUpgrade(self, V4, { kind: "uups" });
            const { chainId } = await ethers.provider.getNetwork();
            const h = ethers.solidityPackedKeccak256(["string", "address", "uint256", "uint256", "address"], ["UPGRADE", impl, 0n, chainId, self]);
            await token.upgradeWithValidatorApproval(impl, await Promise.all(vals.slice(0, 2).map((v) => v.signMessage(ethers.getBytes(h)))));
            const v4 = V4.attach(self);

            // As today until the casters are set: the validators alone mint.
            expect(await v4.getCasters()).to.deep.equal([]);
            expect(await v4.requiredCasterSignatures()).to.equal(0n);
            await mint(v4, vals.slice(0, 2), user.address, 1n, "before-casters");

            const casters = wallets(3);
            await expect(updateCasters(v4, vals, casters.slice(0, 2))).to.be.revertedWith("Caster count out of range");
            await expect(updateCasters(v4, vals, casters)).to.emit(v4, "CastersUpdated");
            expect(await v4.getCasters()).to.deep.equal(casters.map((c) => c.address));
            expect(await v4.requiredCasterSignatures()).to.equal(2n);

            await expect(mint(v4, vals, user.address, 1n, "after-casters")).to.be.revertedWith("Insufficient valid signatures");
            await mint(v4, [...vals.slice(0, 2), ...casters.slice(0, 2)], user.address, 1n, "after-casters");
            await expect(updateCasters(v4, [...vals, ...casters], [], casters.map((c) => c.address))).to.be.revertedWith(
                "Caster count out of range"
            );
        });

        it("a V3 proxy with 4 validators needs a strict majority (3) for a removal proposal after the upgrade, not V3's 2", async function () {
            const vals = wallets(4);
            const V3 = await ethers.getContractFactory("vBTCbV3");
            const token = await upgrades.deployProxy(V3, ["n", "s", vals.map((v) => v.address)], { initializer: "initialize", kind: "uups" });
            const self = await token.getAddress();
            const { chainId } = await ethers.provider.getNetwork();
            expect(await token.requiredRemoveSignatures()).to.equal(2n);              // V3's 51% at 4
            const V4 = await ethers.getContractFactory("vBTCb");
            const impl = await upgrades.prepareUpgrade(self, V4, { kind: "uups" });
            const h = ethers.solidityPackedKeccak256(["string", "address", "uint256", "uint256", "address"], ["UPGRADE", impl, 0n, chainId, self]);
            await token.upgradeWithValidatorApproval(impl, await Promise.all(vals.slice(0, 3).map((v) => v.signMessage(ethers.getBytes(h)))));
            const v4 = V4.attach(self);

            const target = [vals[3].address];
            const propose = async (signers) => {
                const value = { validators: target, casters: [], vfxBlockHeight: 9n, adminNonce: await v4.adminNonce() };
                return v4.proposeRemoval(target, [], 9n, await signTyped(v4, signers, "ProposeRemoval", value));
            };
            await expect(propose(vals.slice(0, 2))).to.be.revertedWith("Insufficient valid admin signatures");
            await expect(propose(vals.slice(0, 3))).to.emit(v4, "RemovalProposed");
        });

        it("the submit script sends only the signatures that count: V3 reverts the whole upgrade on one malformed signature", async function () {
            const common = require("../scripts/upgrade-common");
            const { token, vals, chainId, self } = await loadFixture(deployV3Fixture);
            const V4 = await ethers.getContractFactory("vBTCb");
            const impl = await upgrades.prepareUpgrade(self, V4, { kind: "uups" });
            const hash = ethers.solidityPackedKeccak256(["string", "address", "uint256", "uint256", "address"], ["UPGRADE", impl, 0n, chainId, self]);
            const [s0, s1] = await Promise.all(vals.slice(0, 2).map((v) => v.signMessage(ethers.getBytes(hash))));
            const outsider = await ethers.Wallet.createRandom().signMessage(ethers.getBytes(hash));
            const given = ["0x1234", s0, s0, outsider, s1];

            const { validators, signatures } = await common.validSigners(await common.contractAt(self), { messageVersion: 3, hash }, given);
            expect(validators.length).to.equal(2);
            expect(signatures).to.deep.equal([s0, s1]);
            await expect(token.upgradeWithValidatorApproval(impl, given)).to.be.reverted;
            await token.upgradeWithValidatorApproval(impl, signatures);
            expect(await upgrades.erc1967.getImplementationAddress(self)).to.equal(impl);
        });

        it("keeps every V3 storage slot recorded in the Base mainnet manifest", async function () {
            const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", ".openzeppelin", "base.json"), "utf8"));
            const impls = Object.values(manifest.impls);
            expect(impls.length).to.equal(1);
            const deployed = impls[0].layout.storage;

            const buildInfo = await artifacts.getBuildInfo("contracts/vBTCb.sol:vBTCb");
            const v4 = buildInfo.output.contracts["contracts/vBTCb.sol"]["vBTCb"].storageLayout.storage;
            const end = (s) => BigInt(s.slot) + (s.label === "__gap" ? BigInt(s.type.match(/\)(\d+)_storage/)[1]) : 1n);

            for (const d of deployed.filter((s) => s.label !== "__gap")) {
                const n = v4.find((s) => s.label === d.label);
                expect(n, d.label).to.not.equal(undefined);
                expect([n.slot, n.offset], d.label).to.deep.equal([d.slot, d.offset]);
            }
            const oldGap = deployed.find((s) => s.label === "__gap");
            const newGap = v4.find((s) => s.label === "__gap");
            expect(end(newGap)).to.equal(end(oldGap));
            const added = v4.filter((s) => !deployed.some((d) => d.label === s.label));
            for (const s of added) expect(BigInt(s.slot) < BigInt(newGap.slot), s.label).to.equal(true);
        });
    });
});
