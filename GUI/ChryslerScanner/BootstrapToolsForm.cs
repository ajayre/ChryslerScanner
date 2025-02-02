﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using ChryslerScanner.Helpers;
using ChryslerScanner.Models;
using ChryslerScanner.Services;

namespace ChryslerScanner
{
    public partial class BootstrapToolsForm : Form
    {
        private readonly MainForm OriginalForm;
        private readonly SerialService SerialService;
        private readonly SynchronizationContext UIContext;

        private string SCIBusBootstrapLogFilename;
        private string SCIBusFlashReadFilename;
        private string SCIBusEEPROMReadFilename;
        private byte[] FlashFileBuffer = null;
        private byte[] EEPROMFileBuffer = null;
        private uint FlashChipSize = 0;

        private bool SCIBusResponse = false;
        private bool SCIBusNextRequest = false;
        private byte SCIBusRxRetryCount = 0;
        private byte SCIBusTxRetryCount = 0;
        private bool SCIBusBootstrapFinished = false;
        private bool SCIBusRxTimeout = false;
        private bool SCIBusTxTimeout = false;
        private Task CurrentTask = Task.None;

        private uint SCIBusCurrentMemoryOffset = 0;
        private byte[] SCIBusTxPayload = null;

        private const double MinBattVolts = 11.5; // V
        private const double MinBootVolts = 11.5; // V
        private const double MinProgVolts = 19.5; // V

        private const ushort FlashReadBlockSize = 512;
        private const ushort FlashWriteBlockSize = 512;
        private const ushort EEPROMReadBlockSize = 512;
        private const ushort EEPROMWriteBlockSize = 512;

        private string PartNumberString = string.Empty;
        private string NewPartNumberString = string.Empty;

        private bool FlashChipDetectButtonClicked = false;
        private bool SwitchBackToLSWhenExit = false;
        private bool FlashEraseSuccess = false;

        private System.Timers.Timer SCIBusNextRequestTimer = new System.Timers.Timer();
        private System.Timers.Timer SCIBusRxTimeoutTimer = new System.Timers.Timer();
        private System.Timers.Timer SCIBusTxTimeoutTimer = new System.Timers.Timer();
        private BackgroundWorker SCIBusBootstrapWorker = new BackgroundWorker();

        private enum SCI_ID
        {
            WriteError = 0x01,
            BootstrapBaudrateSet = 0x06,
            UploadWorkerFunctionResult = 0x11,
            StartWorkerFunction = 0x21,
            ExitWorkerFunction = 0x22,
            BootstrapSeedKeyRequest = 0x24,
            BootstrapSeedKeyResponse = 0x26,
            FlashBlockWrite = 0x31,
            FlashBlockRead = 0x34,
            EEPROMBlockWrite = 0x37,
            EEPROMBlockRead = 0x3A,
            StartBootloader = 0x47,
            UploadBootloader = 0x4C,
            BlockSizeError = 0x80,
            EraseError_81 = 0x81,
            EraseError_82 = 0x82,
            EraseError_83 = 0x83,
            OffsetError = 0x84,
            BootstrapModeNotProtected = 0xDB
        }

        private enum Bootloader
        {
            Empty = 0x00,
            SBEC3_SBEC3PLUS_128k = 0x01,
            SBEC3_128k_custom = 0x02,
            SBEC3A_3APLUS_3B_256k = 0x03,
            SBEC3_256k_custom = 0x04,
            EATX3_128k = 0x05,
            EATX3A_256k = 0x06,
            JTEC = 0x07,
            JTECPLUS_256k = 0x08
        }

        private enum WorkerFunction
        {
            Empty = 0x00,
            PartNumberRead = 0x01,
            FlashID = 0x02,
            FlashRead = 0x03,
            FlashErase = 0x04,
            FlashWrite = 0x05,
            VerifyFlashChecksum = 0x06,
            EEPROMRead = 0x07,
            EEPROMWrite = 0x08,
        }

        private enum FlashMemoryManufacturer
        {
            STMicroelectronics = 0x20,
            CATALYST = 0x31,
            Intel = 0x89,
            TexasInstruments = 0x97
        }

        private enum FlashMemoryType
        {
            M28F102 = 0x50,
            CAT28F102 = 0x51,
            N28F010 = 0xB4,
            N28F020 = 0xBD,
            M28F210 = 0xE0,
            M28F220 = 0xE6,
            M28F200T = 0x74,
            M28F200B = 0x75,
            TMS28F210 = 0xE5
        }

        private enum BootloaderError
        {
            OK = 0x00,
            NoResponseToMagicByte = 0x01,
            UnexpectedResponseToMagicByte = 0x02,
            SecuritySeedResponseTimeout = 0x03,
            SecuritySeedChecksumError = 0x04,
            SecurityKeyStatusTimeout = 0x05,
            SecurityKeyNotAccepted = 0x06,
            StartBootloaderTimeout = 0x07,
            UnexpectedBootloaderStatusByte = 0x08
        }

        private enum WorkerFunctionError
        {
            OK = 0x00,
            NoResponseToPing = 0x01,
            UploadInterrupted = 0x02,
            UnexpectedUploadResult = 0x03
        }

        private enum Task
        {
            None,
            CheckVoltages,
            ReadPartNumber,
            DetectFlashMemoryType,
            BackupFlashMemory,
            ReadFlashMemory,
            BackupEEPROM,
            EraseFlashMemory,
            WriteFlashMemory,
            VerifyFlashChecksum,
            UpdateEEPROM,
            ReadEEPROM,
            WriteEEPROM,
            FinishFlashRead,
            FinishFlashWrite,
            FinishEEPROMRead,
            FinishEEPROMWrite
        }

        public BootstrapToolsForm(MainForm IncomingForm, SerialService service)
        {
            OriginalForm = IncomingForm;
            InitializeComponent();
            UIContext = SynchronizationContext.Current;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            SerialService = service;
            SerialService.PacketReceived += PacketReceivedHandler; // subscribe to the PacketReceived event
            OriginalForm.ChangeLanguage();

            BootloaderComboBox.SelectedIndex = 3;
            WorkerFunctionComboBox.SelectedIndex = 0;
            FlashChipComboBox.SelectedIndex = 0;
            SCIBusBootstrapLogFilename = @"LOG/SCI/scibootstraplog_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";

            SCIBusNextRequestTimer.Elapsed += new ElapsedEventHandler(SCIBusNextRequestHandler);
            SCIBusNextRequestTimer.Interval = 25; // ms
            SCIBusNextRequestTimer.AutoReset = false;
            SCIBusNextRequestTimer.Enabled = true;
            SCIBusNextRequestTimer.Start();

            SCIBusRxTimeoutTimer.Elapsed += new ElapsedEventHandler(SCIBusRxTimeoutHandler);
            SCIBusRxTimeoutTimer.Interval = 2000; // ms
            SCIBusRxTimeoutTimer.AutoReset = false;
            SCIBusRxTimeoutTimer.Enabled = true;
            SCIBusRxTimeoutTimer.Stop();

            SCIBusTxTimeoutTimer.Elapsed += new ElapsedEventHandler(SCIBusTxTimeoutHandler);
            SCIBusTxTimeoutTimer.Interval = 2000; // ms
            SCIBusTxTimeoutTimer.AutoReset = false;
            SCIBusTxTimeoutTimer.Enabled = true;
            SCIBusTxTimeoutTimer.Stop();

            SCIBusBootstrapWorker.WorkerReportsProgress = true;
            SCIBusBootstrapWorker.WorkerSupportsCancellation = true;
            SCIBusBootstrapWorker.DoWork += new DoWorkEventHandler(SCIBusBootstrap_DoWork);
            SCIBusBootstrapWorker.ProgressChanged += new ProgressChangedEventHandler(SCIBusBootstrap_ProgressChanged);
            SCIBusBootstrapWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(SCIBusBootstrap_RunWorkerCompleted);

            ActiveControl = BootstrapButton;
        }

        private void BootstrapToolsForm_Load(object sender, EventArgs e)
        {
            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Begin by bootstrapping ECU with selected bootloader.");
        }

        private void SCIBusNextRequestHandler(object source, ElapsedEventArgs e) => SCIBusNextRequest = true;

        private void SCIBusRxTimeoutHandler(object source, ElapsedEventArgs e) => SCIBusRxTimeout = true;

        private void SCIBusTxTimeoutHandler(object source, ElapsedEventArgs e) => SCIBusTxTimeout = true;

        private void SCIBusBootstrap_DoWork(object sender, DoWorkEventArgs e)
        {
            while (!SCIBusBootstrapFinished && !SCIBusBootstrapWorker.CancellationPending)
            {
                Thread.Sleep(5);

                while (!SCIBusNextRequest && !SCIBusBootstrapWorker.CancellationPending) // wait for next request message
                {
                    Thread.Sleep(5);
                }

                if (SCIBusBootstrapWorker.CancellationPending) break;

                SCIBusResponse = false;
                SCIBusNextRequest = false;

                SCIBusBootstrapWorker.ReportProgress(0); // request message is sent in the ProgressChanged event handler method

                while (!SCIBusResponse && !SCIBusRxTimeout && !SCIBusBootstrapWorker.CancellationPending)
                {
                    Thread.Sleep(5);
                }

                if (SCIBusBootstrapWorker.CancellationPending) break;

                if (SCIBusRxTimeout)
                {
                    SCIBusRxRetryCount++;

                    if (SCIBusRxRetryCount > 9)
                    {
                        SCIBusRxRetryCount = 0;
                        e.Cancel = true;
                        break;
                    }

                    SCIBusNextRequest = true;
                    SCIBusRxTimeout = false;
                    SCIBusRxTimeoutTimer.Stop();
                    SCIBusRxTimeoutTimer.Start();
                }

                if (SCIBusResponse)
                {
                    SCIBusRxRetryCount = 0;
                }
            }

            if (SCIBusBootstrapWorker.CancellationPending)
            {
                e.Cancel = true;
            }
        }

        private void SCIBusBootstrap_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage != 0)
                return;

            Invoke((MethodInvoker)delegate
            {
                switch (CurrentTask)
                {
                    case Task.CheckVoltages:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 1. Check voltages.");

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.USB;
                        packet.Command = (byte)PacketHelper.Command.Request;
                        packet.Mode = (byte)PacketHelper.RequestMode.AllVolts;
                        packet.Payload = null;

                        OriginalForm.TransmitUSBPacket("[<-TX] Request voltage measurements:", packet);
                        SerialService.WritePacket(packet);
                        break;
                    }
                    case Task.ReadPartNumber:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 2. Read part number.");
                        WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.PartNumberRead;
                        UploadButton_Click(this, EventArgs.Empty);
                        break;
                    }
                    case Task.DetectFlashMemoryType:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 3. Detect flash memory type.");
                        WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashID;
                        UploadButton_Click(this, EventArgs.Empty);
                        break;
                    }
                    case Task.BackupFlashMemory:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.FlashRead)
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 4. Backup flash memory.");
                            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashRead;
                            SCIBusFlashReadFilename = @"ROMs/PCM/pcm_flash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";
                            PrepareFlashMemoryReading();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 16) & 0xFF);
                        SCIBusTxPayload[2] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
                        SCIBusTxPayload[3] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);

                        if (SCIBusCurrentMemoryOffset >= FlashChipSize) break;

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.ReadFlashMemory:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.FlashRead)
                        {
                            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashRead;
                            PrepareFlashMemoryReading();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 16) & 0xFF);
                        SCIBusTxPayload[2] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
                        SCIBusTxPayload[3] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);

                        if (SCIBusCurrentMemoryOffset >= FlashChipSize) break;

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.BackupEEPROM:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.EEPROMRead)
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM.");
                            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMRead;
                            SCIBusEEPROMReadFilename = @"ROMs/PCM/pcm_eeprom_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";
                            PrepareEEPROMReading();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        if (SCIBusCurrentMemoryOffset >= 0x200) break;

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.EraseFlashMemory:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 6. Erase flash memory.");
                        WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashErase;
                        UploadButton_Click(this, EventArgs.Empty);
                        break;
                    }
                    case Task.WriteFlashMemory:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.FlashWrite)
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 7. Write flash memory.");
                            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashWrite;
                            PrepareFlashMemoryWriting();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 16) & 0xFF);
                        SCIBusTxPayload[2] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
                        SCIBusTxPayload[3] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);

                        if (SCIBusCurrentMemoryOffset >= FlashChipSize) break;

                        Array.Copy(FlashFileBuffer, SCIBusCurrentMemoryOffset, SCIBusTxPayload, 6, FlashWriteBlockSize);

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.SingleVPP;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.VerifyFlashChecksum:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.VerifyFlashChecksum)
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 8. Verify flash checksum.");
                            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.VerifyFlashChecksum;
                            UploadButton_Click(this, EventArgs.Empty);
                        }
                        break;
                    }
                    case Task.UpdateEEPROM:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.EEPROMWrite)
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 9. Update EEPROM.");

                            switch (BootloaderComboBox.SelectedIndex)
                            {
                                case (byte)Bootloader.JTEC:
                                case (byte)Bootloader.JTECPLUS_256k:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMWrite;
                                    break;
                                }
                                default:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMWrite;
                                    break;
                                }
                            }

                            SCIBusTxPayload = new byte[6] { 0x36, 0x00, 0x00, 0x00, 0x01, 0xFF };
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        //Packet packet = new Packet();

                        //packet.Bus = (byte)PacketHelper.Bus.PCM;
                        //packet.Command = (byte)PacketHelper.Command.MsgTx;
                        //packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        //packet.Payload = SCIBusTxPayload;

                        //OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        //SerialService.WritePacket(packet);

                        //SCIBusRxTimeout = false;
                        //SCIBusRxTimeoutTimer.Stop();
                        //SCIBusRxTimeoutTimer.Start();

                        ExitButton_Click(this, EventArgs.Empty);
                        SCIBusResponse = true;
                        break;
                    }
                    case Task.ReadEEPROM:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.EEPROMRead)
                        {
                            switch (BootloaderComboBox.SelectedIndex)
                            {
                                case (byte)Bootloader.JTEC:
                                case (byte)Bootloader.JTECPLUS_256k:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMRead;
                                    break;
                                }
                                default:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMRead;
                                    break;
                                }
                            }

                            PrepareEEPROMReading();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        if (SCIBusCurrentMemoryOffset >= 0x200) break;

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.WriteEEPROM:
                    {
                        if (WorkerFunctionComboBox.SelectedIndex != (byte)WorkerFunction.EEPROMWrite)
                        {
                            switch (BootloaderComboBox.SelectedIndex)
                            {
                                case (byte)Bootloader.JTEC:
                                case (byte)Bootloader.JTECPLUS_256k:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMWrite;
                                    break;
                                }
                                default:
                                {
                                    WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.EEPROMWrite;
                                    break;
                                }
                            }

                            PrepareEEPROMWriting();
                            UploadButton_Click(this, EventArgs.Empty);
                            break;
                        }

                        if (SCIBusCurrentMemoryOffset >= 0x200) break;

                        Packet packet = new Packet();

                        packet.Bus = (byte)PacketHelper.Bus.PCM;
                        packet.Command = (byte)PacketHelper.Command.MsgTx;
                        packet.Mode = (byte)PacketHelper.MsgTxMode.Single;
                        packet.Payload = SCIBusTxPayload;

                        OriginalForm.TransmitUSBPacket("[<-TX] Send an SCI-bus (PCM) message once:", packet);
                        SerialService.WritePacket(packet);

                        SCIBusRxTimeout = false;
                        SCIBusRxTimeoutTimer.Stop();
                        SCIBusRxTimeoutTimer.Start();
                        break;
                    }
                    case Task.FinishFlashRead:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash memory reading session finished successfully.");
                        SCIBusBootstrapFinished = true;
                        SCIBusResponse = true;
                        break;
                    }
                    case Task.FinishFlashWrite:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash memory writing session finished successfully.");
                        SCIBusBootstrapFinished = true;
                        SCIBusResponse = true;
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Turn key to OFF/LOCKED position.");
                        MessageBox.Show("Success! Turn key to OFF/LOCKED position." + Environment.NewLine + "Wait for 5 seconds before starting the engine.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                    case Task.FinishEEPROMRead:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "EEPROM reading session finished successfully.");
                        SCIBusBootstrapFinished = true;
                        SCIBusResponse = true;
                        break;
                    }
                    case Task.FinishEEPROMWrite:
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "EEPROM writing session finished successfully.");
                        SCIBusBootstrapFinished = true;
                        SCIBusResponse = true;
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Turn key to OFF/LOCKED position.");
                        MessageBox.Show("Success! Turn key to OFF/LOCKED position." + Environment.NewLine + "Wait for a few seconds before starting the engine.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                }

                SCIBusRxTimeout = false;
                SCIBusRxTimeoutTimer.Stop();

                if (CurrentTask == Task.EraseFlashMemory)
                {
                    SCIBusRxTimeoutTimer.Interval = 10000; // ms, erasing takes some time, wait more
                }
                else if (CurrentTask == Task.WriteEEPROM)
                {
                    SCIBusRxTimeoutTimer.Interval = 5000; // ms, EEPROM write takes some time, wait more
                }
                else
                {
                    SCIBusRxTimeoutTimer.Interval = 2000; // ms, return to original timeout
                }

                SCIBusRxTimeoutTimer.Start();
            });
        }

        private void SCIBusBootstrap_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled == true)
            {
                switch (CurrentTask)
                {
                    case Task.BackupFlashMemory:
                    case Task.ReadFlashMemory:
                    case Task.WriteFlashMemory:
                    case Task.BackupEEPROM:
                    case Task.UpdateEEPROM:
                    case Task.ReadEEPROM:
                    case Task.WriteEEPROM:
                    {
                        ExitButton_Click(this, EventArgs.Empty);
                        break;
                    }
                }

                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Current task is cancelled.");
            }

            SCIBusRxTimeout = false;
            SCIBusRxTimeoutTimer.Stop();
            SCIBusTxTimeout = false;
            SCIBusTxTimeoutTimer.Stop();
            SCIBusTxPayload = null;
            EEPROMStopButton.Enabled = true;
            EEPROMReadButton.Enabled = true;
            EEPROMWriteButton.Enabled = true;
            EEPROMBrowseButton.Enabled = true;
            FlashStopButton.Enabled = true;
            FlashReadButton.Enabled = true;
            FlashWriteButton.Enabled = true;
            FlashBrowseButton.Enabled = true;
            FlashChipDetectButton.Enabled = true;
            FlashChipComboBox.Enabled = true;
            ExitButton.Enabled = true;
            StartButton.Enabled = true;
            UploadButton.Enabled = true;
            WorkerFunctionComboBox.Enabled = true;
            BootstrapButton.Enabled = true;
            BootloaderComboBox.Enabled = true;
            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.Empty;
            CurrentTask = Task.None;
        }

        private void BootloaderComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (BootloaderComboBox.SelectedIndex == 7)
            {
                FlashChipComboBox.SelectedIndex = 8; // auto-select "N28F010 (128k+128k)"
            }
        }

        private void BootstrapButton_Click(object sender, EventArgs e)
        {
            string LastPCMSpeed = OriginalForm.PCM.speed;

            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Turn key to OFF/LOCKED position.");

            if (MessageBox.Show("Turn key to OFF/LOCKED position." + Environment.NewLine + "Wait at least 10 seconds afterwards." + Environment.NewLine + "Click OK when done." + Environment.NewLine + Environment.NewLine + "Try again and wait more if bootstrap fails.", "Information", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.OK)
            {
                if (OriginalForm.PCM.speed != "62500 baud")
                {
                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Scanner SCI-bus speed is set to 62500 baud.");
                    OriginalForm.SelectSCIBusHSMode();
                }

                Packet packet = new Packet();

                packet.Bus = (byte)PacketHelper.Bus.USB;
                packet.Command = (byte)PacketHelper.Command.Settings;
                packet.Mode = (byte)PacketHelper.SettingsMode.SetProgVolt;
                packet.Payload = new byte[1] { 0x80 };

                OriginalForm.TransmitUSBPacket("[<-TX] Apply VBB to SCI-RX pin:", packet);
                SerialService.WritePacket(packet);

                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Turn key to RUN position.");

                if (MessageBox.Show("Turn key to RUN position." + Environment.NewLine + "Do not start the engine." + Environment.NewLine + Environment.NewLine + "Click OK when done.", "Information", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.OK)
                {
                    packet = new Packet();

                    packet.Bus = (byte)PacketHelper.Bus.USB;
                    packet.Command = (byte)PacketHelper.Command.Settings;
                    packet.Mode = (byte)PacketHelper.SettingsMode.SetProgVolt;
                    packet.Payload = new byte[1] { 0x00 };

                    OriginalForm.TransmitUSBPacket("[<-TX] Remove VBB from SCI-RX pin:", packet);
                    SerialService.WritePacket(packet);

                    packet = new Packet();

                    packet.Bus = (byte)PacketHelper.Bus.USB;
                    packet.Command = (byte)PacketHelper.Command.Debug;
                    packet.Mode = (byte)PacketHelper.DebugMode.InitBootstrapMode;
                    packet.Payload = new byte[2] { (byte)BootloaderComboBox.SelectedIndex, (byte)FlashChipComboBox.SelectedIndex };

                    OriginalForm.TransmitUSBPacket("[<-TX] Init PCM bootstrap mode:", packet);
                    SerialService.WritePacket(packet);

                    switch ((byte)BootloaderComboBox.SelectedIndex)
                    {
                        case (byte)Bootloader.SBEC3_SBEC3PLUS_128k:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: SBEC3/SBEC3+ (128k).");
                            break;
                        }
                        case (byte)Bootloader.SBEC3_128k_custom:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: SBEC3 (128k) custom.");
                            break;
                        }
                        case (byte)Bootloader.SBEC3A_3APLUS_3B_256k:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: SBEC3A/3A+/3B (256k).");
                            break;
                        }
                        case (byte)Bootloader.SBEC3_256k_custom:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: SBEC3A (256k) custom.");
                            break;
                        }
                        case (byte)Bootloader.EATX3_128k:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: EATX3 (128k).");
                            break;
                        }
                        case (byte)Bootloader.EATX3A_256k:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: EATX3A (256k).");
                            break;
                        }
                        case (byte)Bootloader.JTEC:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: JTEC (256k).");
                            break;
                        }
                        case (byte)Bootloader.JTECPLUS_256k:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: JTEC+ (256k).");
                            break;
                        }
                        case (byte)Bootloader.Empty:
                        default:
                        {
                            OriginalForm.UpdateUSBTextBox("[INFO] Bootloader: empty.");
                            break;
                        }
                    }

                    SwitchBackToLSWhenExit = true;
                }
                else
                {
                    packet = new Packet();

                    packet.Bus = (byte)PacketHelper.Bus.USB;
                    packet.Command = (byte)PacketHelper.Command.Settings;
                    packet.Mode = (byte)PacketHelper.SettingsMode.SetProgVolt;
                    packet.Payload = new byte[1] { 0x00 };

                    OriginalForm.TransmitUSBPacket("[<-TX] Remove VBB from SCI-RX pin:", packet);
                    SerialService.WritePacket(packet);

                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "ECU bootstrapping is cancelled.");

                    if (LastPCMSpeed != "62500 baud")
                    {
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Scanner SCI-bus speed is set to 7812.5 baud.");
                        OriginalForm.SelectSCIBusLSMode();
                    }
                }
            }
            else
            {
                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "ECU bootstrapping is cancelled.");

                if (LastPCMSpeed != "62500 baud")
                {
                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Scanner SCI-bus speed is set to 7812.5 baud.");
                    OriginalForm.SelectSCIBusLSMode();
                }
            }
        }

        private void UploadButton_Click(object sender, EventArgs e)
        {
            Packet packet = new Packet();

            packet.Bus = (byte)PacketHelper.Bus.USB;
            packet.Command = (byte)PacketHelper.Command.Debug;
            packet.Mode = (byte)PacketHelper.DebugMode.UploadWorkerFunction;
            packet.Payload = new byte[2] { (byte)WorkerFunctionComboBox.SelectedIndex, (byte)FlashChipComboBox.SelectedIndex };

            OriginalForm.TransmitUSBPacket("[<-TX] Upload worker function:", packet);
            SerialService.WritePacket(packet);

            Invoke((MethodInvoker)delegate
            {
                switch (WorkerFunctionComboBox.SelectedIndex)
                {
                    case (byte)WorkerFunction.PartNumberRead:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: part number read.");
                        break;
                    }
                    case (byte)WorkerFunction.FlashID:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: flash ID.");
                        break;
                    }
                    case (byte)WorkerFunction.FlashRead:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: flash read.");
                        break;
                    }
                    case (byte)WorkerFunction.FlashErase:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: flash erase.");
                        FlashEraseSuccess = false;
                        break;
                    }
                    case (byte)WorkerFunction.FlashWrite:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: flash write.");
                        break;
                    }
                    case (byte)WorkerFunction.VerifyFlashChecksum:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: verify flash checksum.");
                        break;
                    }
                    case (byte)WorkerFunction.EEPROMRead:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: EEPROM read.");
                        break;
                    }
                    case (byte)WorkerFunction.EEPROMWrite:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: EEPROM write.");
                        break;
                    }
                    case (byte)WorkerFunction.Empty:
                    default:
                    {
                        OriginalForm.UpdateUSBTextBox("[INFO] Worker function: empty.");
                        break;
                    }
                }
            });
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            Packet packet = new Packet();

            packet.Bus = (byte)PacketHelper.Bus.USB;
            packet.Command = (byte)PacketHelper.Command.Debug;
            packet.Mode = (byte)PacketHelper.DebugMode.StartWorkerFunction;
            packet.Payload = new byte[2] { (byte)WorkerFunctionComboBox.SelectedIndex, (byte)FlashChipComboBox.SelectedIndex };

            OriginalForm.TransmitUSBPacket("[<-TX] Start worker function:", packet);
            SerialService.WritePacket(packet);
        }

        private void ExitButton_Click(object sender, EventArgs e)
        {
            Packet packet = new Packet();

            packet.Bus = (byte)PacketHelper.Bus.USB;
            packet.Command = (byte)PacketHelper.Command.Debug;
            packet.Mode = (byte)PacketHelper.DebugMode.ExitWorkerFunction;
            packet.Payload = new byte[2] { (byte)WorkerFunctionComboBox.SelectedIndex, (byte)FlashChipComboBox.SelectedIndex };

            OriginalForm.TransmitUSBPacket("[<-TX] Exit worker function:", packet);
            SerialService.WritePacket(packet);
        }

        private void FlashChipComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!FlashChipComboBox.IsHandleCreated)
                return;

            Invoke((MethodInvoker)delegate
            {
                if (FlashChipComboBox.SelectedIndex == 0)
                {
                    FlashChipSize = 0; // bytes
                    return;
                }

                switch (FlashChipComboBox.SelectedIndex)
                {
                    case 1:
                    case 2:
                    case 3:
                    {
                        FlashChipSize = 131072; // bytes
                        break;
                    }
                    case 4:
                    case 5:
                    case 6:
                    case 7:
                    case 8:
                    case 9:
                    default:
                    {
                        FlashChipSize = 262144; // bytes
                        break;
                    }
                }
            });
        }

        private void FlashChipDetectButton_Click(object sender, EventArgs e)
        {
            WorkerFunctionComboBox.SelectedIndex = (byte)WorkerFunction.FlashID;
            UploadButton_Click(this, EventArgs.Empty);
            FlashChipDetectButtonClicked = true;
        }

        private void FlashBrowseButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog OpenFlashFileDialog = new OpenFileDialog())
            {
                OpenFlashFileDialog.InitialDirectory = Path.Combine(Application.StartupPath, @"ROMs\PCM");
                OpenFlashFileDialog.Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
                OpenFlashFileDialog.FilterIndex = 2;
                OpenFlashFileDialog.RestoreDirectory = false;

                if (OpenFlashFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                using (var FileStream = File.Open(OpenFlashFileDialog.FileName, FileMode.Open))
                {
                    using (var MemoryStream = new MemoryStream())
                    {
                        FileStream.CopyTo(MemoryStream);
                        FlashFileBuffer = MemoryStream.ToArray();

                        if (Path.GetFileName(OpenFlashFileDialog.FileName).Length > 29)
                        {
                            FlashFileNameLabel.Text = Path.GetFileName(OpenFlashFileDialog.FileName).Remove(29);
                        }
                        else
                        {
                            FlashFileNameLabel.Text = Path.GetFileName(OpenFlashFileDialog.FileName);
                        }

                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash file is loaded.");
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Name: " + Path.GetFileName(OpenFlashFileDialog.FileName));
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Size: " + FlashFileBuffer.Length + " bytes = " + ((double)FlashFileBuffer.Length / 1024.0).ToString("0.00") + " kilobytes.");

                        if (FlashChipComboBox.SelectedIndex == 0) return;
                            
                        if ((FlashFileBuffer != null) && (FlashFileBuffer.Length > 0) && (FlashFileBuffer.Length != FlashChipSize))
                        {
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash file size (" + FlashFileBuffer.Length.ToString() + " bytes) must be equal to the flash memory chip size (" + FlashChipSize.ToString() + " bytes)!");
                        }
                    }
                }
            }
        }

        private void FlashWriteButton_Click(object sender, EventArgs e)
        {
            if (SCIBusBootstrapWorker.IsBusy)
            {
                MessageBox.Show("Busy!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            WorkerFunctionComboBox.SelectedIndex = 0;

            if (FlashFileBuffer == null)
            {
                MessageBox.Show("Browse flash file first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show("Are you sure you want to continue?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
            {
                return;
            }
            
            if (FlashChipComboBox.SelectedIndex != 0)
            {
                if (MessageBox.Show("Do you want to use selected flash chip?" + Environment.NewLine + "Yes = use selected chip." + Environment.NewLine + "No = autodetect chip.", "Query", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.No)
                {
                    FlashChipComboBox.SelectedIndex = 0;
                }
            }

            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Start flash memory writing session.");

            CurrentTask = Task.CheckVoltages;
            //CurrentTask = Task.VerifyFlashChecksum; // debug, skip time consuming stuff
            SCIBusBootstrapFinished = false;
            SCIBusNextRequest = true;
            EEPROMStopButton.Enabled = false;
            EEPROMReadButton.Enabled = false;
            EEPROMWriteButton.Enabled = false;
            EEPROMBrowseButton.Enabled = false;
            FlashReadButton.Enabled = false;
            FlashWriteButton.Enabled = false;
            FlashBrowseButton.Enabled = false;
            FlashChipDetectButton.Enabled = false;
            FlashChipComboBox.Enabled = false;
            ExitButton.Enabled = false;
            StartButton.Enabled = false;
            UploadButton.Enabled = false;
            WorkerFunctionComboBox.Enabled = false;
            BootstrapButton.Enabled = false;
            BootloaderComboBox.Enabled = false;
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";
            SCIBusBootstrapWorker.RunWorkerAsync();
        }

        private void FlashReadButton_Click(object sender, EventArgs e)
        {
            if (SCIBusBootstrapWorker.IsBusy)
            {
                MessageBox.Show("Busy!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (FlashChipComboBox.SelectedIndex == 0)
            {
                MessageBox.Show("Detect flash memory chip first.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            WorkerFunctionComboBox.SelectedIndex = 0;

            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Start flash memory reading session.");

            using (SaveFileDialog SaveFlashFileDialog = new SaveFileDialog())
            {
                SaveFlashFileDialog.InitialDirectory = Path.Combine(Application.StartupPath, @"ROMs\PCM");
                SaveFlashFileDialog.Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
                SaveFlashFileDialog.FilterIndex = 2;
                SaveFlashFileDialog.RestoreDirectory = false;

                if (SaveFlashFileDialog.ShowDialog() != DialogResult.OK)
                {
                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash memory reading is cancelled.");
                    return;
                }

                SCIBusFlashReadFilename = SaveFlashFileDialog.FileName;

                if (File.Exists(SaveFlashFileDialog.FileName)) File.Delete(SaveFlashFileDialog.FileName);

                if (Path.GetFileName(SaveFlashFileDialog.FileName).Length > 29)
                {
                    FlashFileNameLabel.Text = Path.GetFileName(SaveFlashFileDialog.FileName).Remove(29);
                }
                else
                {
                    FlashFileNameLabel.Text = Path.GetFileName(SaveFlashFileDialog.FileName);
                }

                CurrentTask = Task.ReadFlashMemory;
                SCIBusBootstrapFinished = false;
                SCIBusNextRequest = true;
                EEPROMStopButton.Enabled = false;
                EEPROMReadButton.Enabled = false;
                EEPROMWriteButton.Enabled = false;
                EEPROMBrowseButton.Enabled = false;
                FlashReadButton.Enabled = false;
                FlashWriteButton.Enabled = false;
                FlashBrowseButton.Enabled = false;
                FlashChipDetectButton.Enabled = false;
                FlashChipComboBox.Enabled = false;
                ExitButton.Enabled = false;
                StartButton.Enabled = false;
                UploadButton.Enabled = false;
                WorkerFunctionComboBox.Enabled = false;
                BootstrapButton.Enabled = false;
                BootloaderComboBox.Enabled = false;
                SCIBusCurrentMemoryOffset = 0;
                SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";
                SCIBusBootstrapWorker.RunWorkerAsync();
            }
        }

        private void FlashStopButton_Click(object sender, EventArgs e)
        {
            if (SCIBusBootstrapWorker.IsBusy && SCIBusBootstrapWorker.WorkerSupportsCancellation)
            {
                if (MessageBox.Show("Are you sure you want to stop flash operation?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
                {
                    return;
                }

                SCIBusBootstrapWorker.CancelAsync();
            }
        }

        private void FlashMemoryBackupCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (FlashMemoryBackupCheckBox.Checked)
                return;

            if (MessageBox.Show("Skipping flash memory backup could lead to data loss." + Environment.NewLine + "Do you really want to continue without backup?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
            {
                FlashMemoryBackupCheckBox.Checked = true;
            }
        }

        private void EEPROMBrowseButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog OpenEEPROMFileDialog = new OpenFileDialog())
            {
                OpenEEPROMFileDialog.InitialDirectory = Path.Combine(Application.StartupPath, @"ROMs\PCM");
                OpenEEPROMFileDialog.Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
                OpenEEPROMFileDialog.FilterIndex = 2;
                OpenEEPROMFileDialog.RestoreDirectory = false;

                if (OpenEEPROMFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                using (var FileStream = File.Open(OpenEEPROMFileDialog.FileName, FileMode.Open))
                {
                    using (var MemoryStream = new MemoryStream())
                    {
                        FileStream.CopyTo(MemoryStream);

                        if (MemoryStream.Length != 512)
                        {
                            EEPROMFileBuffer = null;
                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Invalid EEPROM file size (" + EEPROMFileBuffer.Length + " bytes)." + Environment.NewLine + "Valid EEPROM size is 512 bytes.");
                            return;
                        }

                        EEPROMFileBuffer = MemoryStream.ToArray();

                        if (Path.GetFileName(OpenEEPROMFileDialog.FileName).Length > 29)
                        {
                            EEPROMFileNameLabel.Text = Path.GetFileName(OpenEEPROMFileDialog.FileName).Remove(29);
                        }
                        else
                        {
                            EEPROMFileNameLabel.Text = Path.GetFileName(OpenEEPROMFileDialog.FileName);
                        }

                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "EEPROM file is loaded.");
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Name: " + Path.GetFileName(OpenEEPROMFileDialog.FileName));
                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Size: " + EEPROMFileBuffer.Length + " bytes = " + ((double)EEPROMFileBuffer.Length / 1024.0).ToString("0.00") + " kilobytes.");
                    }
                }
            }
        }

        private void EEPROMWriteButton_Click(object sender, EventArgs e)
        {
            if ((BootloaderComboBox.SelectedIndex == (byte)Bootloader.JTEC) ||
                (BootloaderComboBox.SelectedIndex == (byte)Bootloader.JTECPLUS_256k))
            {
                MessageBox.Show("JTEC EEPROM writing is not supported yet." + Environment.NewLine + Environment.NewLine + "Instead, use \"Tools / Read/Write memory\" menu in normal key-on mode.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (SCIBusBootstrapWorker.IsBusy)
            {
                MessageBox.Show("Busy!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (EEPROMFileBuffer == null)
            {
                MessageBox.Show("Browse EEPROM file first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (EEPROMFileBuffer.Length != 512)
            {
                MessageBox.Show("EEPROM file size incorrect!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            WorkerFunctionComboBox.SelectedIndex = 0;

            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Start EEPROM writing session.");

            if (EEPROMFileBuffer == null)
            {
                EEPROMBrowseButton_Click(this, EventArgs.Empty);
            }

            if (EEPROMFileBuffer == null)
            {
                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "EEPROM writing is cancelled.");
                return;
            }

            CurrentTask = Task.WriteEEPROM;
            SCIBusBootstrapFinished = false;
            SCIBusNextRequest = true;
            EEPROMReadButton.Enabled = false;
            EEPROMWriteButton.Enabled = false;
            EEPROMBrowseButton.Enabled = false;
            FlashStopButton.Enabled = false;
            FlashReadButton.Enabled = false;
            FlashWriteButton.Enabled = false;
            FlashBrowseButton.Enabled = false;
            FlashChipDetectButton.Enabled = false;
            FlashChipComboBox.Enabled = false;
            ExitButton.Enabled = false;
            StartButton.Enabled = false;
            UploadButton.Enabled = false;
            WorkerFunctionComboBox.Enabled = false;
            BootstrapButton.Enabled = false;
            BootloaderComboBox.Enabled = false;
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";
            SCIBusBootstrapWorker.RunWorkerAsync();
        }

        private void EEPROMReadButton_Click(object sender, EventArgs e)
        {
            if ((BootloaderComboBox.SelectedIndex == (byte)Bootloader.JTEC) ||
                (BootloaderComboBox.SelectedIndex == (byte)Bootloader.JTECPLUS_256k))
            {
                MessageBox.Show("JTEC EEPROM reading is not supported yet." + Environment.NewLine + Environment.NewLine + "Instead, use \"Tools / Read/Write memory\" menu in normal key-on mode.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (SCIBusBootstrapWorker.IsBusy)
            {
                MessageBox.Show("Busy!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            WorkerFunctionComboBox.SelectedIndex = 0;

            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Start EEPROM reading session.");

            using (SaveFileDialog SaveEEPROMFileDialog = new SaveFileDialog())
            {
                SaveEEPROMFileDialog.InitialDirectory = Path.Combine(Application.StartupPath, @"ROMs\PCM");
                SaveEEPROMFileDialog.Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
                SaveEEPROMFileDialog.FilterIndex = 2;
                SaveEEPROMFileDialog.RestoreDirectory = false;

                if (SaveEEPROMFileDialog.ShowDialog() != DialogResult.OK)
                {
                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "EEPROM reading is cancelled.");
                    return;
                }

                SCIBusEEPROMReadFilename = SaveEEPROMFileDialog.FileName;

                if (File.Exists(SaveEEPROMFileDialog.FileName)) File.Delete(SaveEEPROMFileDialog.FileName);

                if (Path.GetFileName(SaveEEPROMFileDialog.FileName).Length > 29)
                {
                    EEPROMFileNameLabel.Text = Path.GetFileName(SaveEEPROMFileDialog.FileName).Remove(29);
                }
                else
                {
                    EEPROMFileNameLabel.Text = Path.GetFileName(SaveEEPROMFileDialog.FileName);
                }

                CurrentTask = Task.ReadEEPROM;
                SCIBusBootstrapFinished = false;
                SCIBusNextRequest = true;
                EEPROMReadButton.Enabled = false;
                EEPROMWriteButton.Enabled = false;
                EEPROMBrowseButton.Enabled = false;
                FlashStopButton.Enabled = false;
                FlashReadButton.Enabled = false;
                FlashWriteButton.Enabled = false;
                FlashBrowseButton.Enabled = false;
                FlashChipDetectButton.Enabled = false;
                FlashChipComboBox.Enabled = false;
                ExitButton.Enabled = false;
                StartButton.Enabled = false;
                UploadButton.Enabled = false;
                WorkerFunctionComboBox.Enabled = false;
                BootstrapButton.Enabled = false;
                BootloaderComboBox.Enabled = false;
                SCIBusCurrentMemoryOffset = 0;
                SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";
                SCIBusBootstrapWorker.RunWorkerAsync();
            }
        }

        private void EEPROMStopButton_Click(object sender, EventArgs e)
        {
            if (SCIBusBootstrapWorker.IsBusy && SCIBusBootstrapWorker.WorkerSupportsCancellation)
            {
                if (MessageBox.Show("Are you sure you want to stop EEPROM operation?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
                {
                    return;
                }

                SCIBusBootstrapWorker.CancelAsync();
            }
        }

        private void EEPROMBackupCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (EEPROMBackupCheckBox.Checked)
                return;

            if (MessageBox.Show("Skipping EEPROM backup could lead to data loss." + Environment.NewLine + "Do you really want to continue without backup?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
            {
                EEPROMBackupCheckBox.Checked = true;
            }
        }

        private void WorkerFunctionComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (WorkerFunctionComboBox.SelectedIndex)
            {
                case (byte)WorkerFunction.Empty:
                case (byte)WorkerFunction.PartNumberRead:
                case (byte)WorkerFunction.FlashID:
                case (byte)WorkerFunction.FlashErase:
                case (byte)WorkerFunction.VerifyFlashChecksum:
                {
                    ExitButton.Enabled = false;
                    break;
                }
                case (byte)WorkerFunction.FlashRead:
                case (byte)WorkerFunction.FlashWrite:
                case (byte)WorkerFunction.EEPROMRead:
                case (byte)WorkerFunction.EEPROMWrite:
                {
                    if (!SCIBusBootstrapWorker.IsBusy) ExitButton.Enabled = true;
                    break;
                }
            }
        }

        private void PacketReceivedHandler(object sender, Packet packet)
        {
            UIContext.Post(state =>
            {
                switch (packet.Bus)
                {
                    case (byte)PacketHelper.Bus.USB:
                    {
                        switch (packet.Command)
                        {
                            case (byte)PacketHelper.Command.Settings:
                            {
                                switch (packet.Mode)
                                {
                                    case (byte)PacketHelper.SettingsMode.SetProgVolt:
                                    {
                                        if (packet.Payload.Length == 0) break;

                                        if (packet.Payload[0] == 0)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "VBB/VPP removed from SCI-RX pin.");

                                            if (!SCIBusBootstrapWorker.IsBusy) break;

                                            switch (CurrentTask)
                                            {
                                                case Task.DetectFlashMemoryType:
                                                {
                                                    if (FlashMemoryBackupCheckBox.Checked)
                                                    {
                                                        CurrentTask = Task.BackupFlashMemory;
                                                    }
                                                    else
                                                    {
                                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 4. Backup flash memory." + Environment.NewLine + Environment.NewLine + "Skip flash memory backup.");

                                                        if (EEPROMBackupCheckBox.Checked)
                                                        {
                                                            switch (BootloaderComboBox.SelectedIndex)
                                                            {
                                                                case (byte)Bootloader.JTEC:
                                                                case (byte)Bootloader.JTECPLUS_256k:
                                                                {
                                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");
                                                                    CurrentTask = Task.EraseFlashMemory;
                                                                    break;
                                                                }
                                                                default:
                                                                {
                                                                    CurrentTask = Task.BackupEEPROM;
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");
                                                            CurrentTask = Task.EraseFlashMemory;
                                                        }
                                                    }
                                                    SCIBusResponse = true;
                                                    break;
                                                }
                                                case Task.EraseFlashMemory:
                                                {
                                                    if (!FlashEraseSuccess)
                                                    {
                                                        SCIBusBootstrapWorker.CancelAsync();
                                                        break;
                                                    }

                                                    CurrentTask = Task.WriteFlashMemory;
                                                    SCIBusResponse = true;
                                                    break;
                                                }
                                            }
                                        }
                                        else if (Util.IsBitSet(packet.Payload[0], 7))
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Apply VBB (12V) to SCI-RX pin.");
                                        }
                                        else if (Util.IsBitSet(packet.Payload[0], 6))
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Apply VPP (20V) to SCI-RX pin.");
                                        }
                                        else
                                        {
                                            // TODO
                                        }
                                        break;
                                    }
                                }
                                break;
                            }
                            case (byte)PacketHelper.Command.Response:
                            {
                                switch (packet.Mode)
                                {
                                    case (byte)PacketHelper.ResponseMode.AllVolts:
                                    {
                                        double BatteryVoltage = ((packet.Payload[0] << 8) + packet.Payload[1]) / 1000.00;
                                        double BootstrapVoltage = ((packet.Payload[2] << 8) + packet.Payload[3]) / 1000.00;
                                        double ProgrammingVoltage = ((packet.Payload[4] << 8) + packet.Payload[5]) / 1000.00;
                                        string BatteryVoltageString = BatteryVoltage.ToString("0.000") + " V";
                                        string BootstrapVoltageString = BootstrapVoltage.ToString("0.000") + " V";
                                        string ProgrammingVoltageString = ProgrammingVoltage.ToString("0.000") + " V";

                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Battery voltage: " + BatteryVoltageString + Environment.NewLine + "Bootstrap voltage: " + BootstrapVoltageString + Environment.NewLine + "Programming voltage: " + ProgrammingVoltageString);

                                        if ((BatteryVoltage >= MinBattVolts) && (BootstrapVoltage >= MinBootVolts) && (ProgrammingVoltage >= MinProgVolts))
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "All voltages are nominal.");

                                            if (!SCIBusBootstrapWorker.IsBusy) break;

                                            switch (BootloaderComboBox.SelectedIndex)
                                            {
                                                case (byte)Bootloader.JTEC:
                                                case (byte)Bootloader.JTECPLUS_256k:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 2. Read part number." + Environment.NewLine + Environment.NewLine + "Skip part number read.");

                                                    if (FlashChipComboBox.SelectedIndex == 0)
                                                    {
                                                        CurrentTask = Task.DetectFlashMemoryType;
                                                    }
                                                    else
                                                    {
                                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 3. Detect flash memory type." + Environment.NewLine + Environment.NewLine + "Use selected flash chip.");

                                                        if ((FlashFileBuffer != null) && (FlashFileBuffer.Length > 0) && (FlashFileBuffer.Length != FlashChipSize))
                                                        {
                                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash file size (" + FlashFileBuffer.Length.ToString() + " bytes) must be equal to the flash memory chip size (" + FlashChipSize.ToString() + " bytes)!");

                                                            SCIBusBootstrapWorker.CancelAsync();
                                                        }

                                                        if (FlashMemoryBackupCheckBox.Checked)
                                                        {
                                                            CurrentTask = Task.BackupFlashMemory;
                                                        }
                                                        else
                                                        {
                                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 4. Backup flash memory." + Environment.NewLine + Environment.NewLine + "Skip flash memory backup.");
                                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");

                                                            CurrentTask = Task.EraseFlashMemory;
                                                        }
                                                    }
                                                    break;
                                                }
                                                default:
                                                {
                                                    CurrentTask = Task.ReadPartNumber;
                                                    break;
                                                }
                                            }

                                            SCIBusResponse = true;
                                        }
                                        else
                                        {
                                            if (BatteryVoltage < MinBattVolts)
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Battery voltage must be above " + MinBattVolts.ToString("0.0") + "V.");
                                            }
                                            if (BootstrapVoltage < MinBootVolts)
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap voltage must be above " + MinBootVolts.ToString("0.0") + "V.");
                                            }
                                            if (ProgrammingVoltage < MinProgVolts)
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Programming voltage must be above " + MinProgVolts.ToString("0.0") + "V.");
                                            }

                                            if (SCIBusBootstrapWorker.IsBusy)
                                            {
                                                SCIBusBootstrapWorker.CancelAsync();
                                            }
                                        }
                                        break;
                                    }
                                }
                                break;
                            }
                            case (byte)PacketHelper.Command.Debug:
                                switch (packet.Mode)
                                {
                                    case (byte)PacketHelper.DebugMode.InitBootstrapMode:
                                    {
                                        switch (packet.Payload[0])
                                        {
                                            case (byte)BootloaderError.OK:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap mode initialized successfully.");
                                                break;
                                            }
                                            case (byte)BootloaderError.NoResponseToMagicByte:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: no response to magic byte.");
                                                break;
                                            }
                                            case (byte)BootloaderError.UnexpectedResponseToMagicByte:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: unexpected response to magic byte.");
                                                break;
                                            }
                                            case (byte)BootloaderError.SecuritySeedResponseTimeout:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: security seed response timeout.");
                                                break;
                                            }
                                            case (byte)BootloaderError.SecuritySeedChecksumError:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: security seed checksum error.");
                                                break;
                                            }
                                            case (byte)BootloaderError.SecurityKeyStatusTimeout:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: security key status timeout.");
                                                break;
                                            }
                                            case (byte)BootloaderError.SecurityKeyNotAccepted:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: security key not accepted.");
                                                break;
                                            }
                                            case (byte)BootloaderError.StartBootloaderTimeout:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: start bootloader timeout.");
                                                break;
                                            }
                                            case (byte)BootloaderError.UnexpectedBootloaderStatusByte:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: unexpected bootloader status byte.");
                                                break;
                                            }
                                            default:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap status: unknown.");
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                    case (byte)PacketHelper.DebugMode.UploadWorkerFunction:
                                    {
                                        switch (packet.Payload[0])
                                        {
                                            case (byte)WorkerFunctionError.OK:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Worker function uploaded successfully.");

                                                if (!SCIBusBootstrapWorker.IsBusy)
                                                {
                                                    if (FlashChipDetectButtonClicked)
                                                    {
                                                        FlashChipDetectButtonClicked = false;
                                                        StartButton_Click(this, EventArgs.Empty);
                                                    }

                                                    break;
                                                }

                                                switch (CurrentTask)
                                                {
                                                    case Task.ReadPartNumber:
                                                    case Task.DetectFlashMemoryType:
                                                    case Task.BackupFlashMemory:
                                                    case Task.ReadFlashMemory:
                                                    case Task.BackupEEPROM:
                                                    case Task.EraseFlashMemory:
                                                    case Task.WriteFlashMemory:
                                                    case Task.VerifyFlashChecksum:
                                                    case Task.UpdateEEPROM:
                                                    case Task.ReadEEPROM:
                                                    case Task.WriteEEPROM:
                                                    {
                                                        StartButton_Click(this, EventArgs.Empty);
                                                        break;
                                                    }
                                                }
                                                break;
                                            }
                                            case (byte)WorkerFunctionError.NoResponseToPing:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Worker function status: no response to ping.");

                                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                                SCIBusBootstrapWorker.CancelAsync();
                                                break;
                                            }
                                            case (byte)WorkerFunctionError.UploadInterrupted:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Worker function status: upload interrupted.");

                                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                                SCIBusBootstrapWorker.CancelAsync();
                                                break;
                                            }
                                            case (byte)WorkerFunctionError.UnexpectedUploadResult:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Worker function status: unexptected upload result.");

                                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                                SCIBusBootstrapWorker.CancelAsync();
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                        break;
                    }
                    case (byte)PacketHelper.Bus.PCM:
                    case (byte)PacketHelper.Bus.TCM:
                    {
                        byte[] SCIBusResponseBytes = packet.Payload.Skip(4).ToArray(); // skip 4 timestamp bytes

                        if (SCIBusResponseBytes.Length == 0) break;

                        switch (SCIBusResponseBytes[0])
                        {
                            case (byte)SCI_ID.BootstrapBaudrateSet:
                            {
                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Set bootstrap baudrate to 62500 baud. OK.");
                                break;
                            }
                            case (byte)SCI_ID.UploadWorkerFunctionResult:
                            {
                                if (SCIBusResponseBytes.Length < 2) break;

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Upload worker function: ");

                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.PartNumberRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "part number read.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashID:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "flash ID.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "flash read.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "flash erase.");
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Erase algorithm used: " + FlashChipComboBox.Items[(byte)FlashChipComboBox.SelectedIndex].ToString() + ".");
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "flash write.");
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Write algorithm used: " + FlashChipComboBox.Items[(byte)FlashChipComboBox.SelectedIndex].ToString() + ".");
                                        break;
                                    }
                                    case (byte)WorkerFunction.VerifyFlashChecksum:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "verify flash checksum.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "EEPROM read.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "EEPROM write.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.Empty:
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "empty.");
                                        break;
                                    }
                                }
                                break;
                            }
                            case (byte)SCI_ID.StartWorkerFunction:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.PartNumberRead:
                                    {
                                        if (SCIBusResponseBytes.Length < 30)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Part number: unknown.");
                                            break;
                                        }

                                        if (SCIBusResponseBytes[1] != 0xFF)
                                        {
                                            PartNumberString = Util.ByteToHexString(SCIBusResponseBytes, 1, 4).Replace(" ", "");

                                            if ((SCIBusResponseBytes[5] >= 0x41) && (SCIBusResponseBytes[5] <= 0x5A) && (SCIBusResponseBytes[6] >= 0x41) && (SCIBusResponseBytes[6] <= 0x5A))
                                            {
                                                PartNumberString += Encoding.ASCII.GetString(SCIBusResponseBytes, 5, 2);
                                            }
                                            else // no revision label available, append 99 by default
                                            {
                                                PartNumberString += "99";
                                            }
                                        }
                                        else if (SCIBusResponseBytes[21] != 0xFF)
                                        {
                                            PartNumberString = Util.ByteToHexString(SCIBusResponseBytes, 21, 4).Replace(" ", "");

                                            if ((SCIBusResponseBytes[25] >= 0x41) && (SCIBusResponseBytes[25] <= 0x5A) && (SCIBusResponseBytes[26] >= 0x41) && (SCIBusResponseBytes[26] <= 0x5A))
                                            {
                                                PartNumberString += Encoding.ASCII.GetString(SCIBusResponseBytes, 25, 2);
                                            }
                                            else // no revision label available, append 99 by default
                                            {
                                                PartNumberString += "99";
                                            }
                                        }
                                        else
                                        {
                                            PartNumberString = string.Empty;
                                        }

                                        if (PartNumberString != string.Empty)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Part number: " + PartNumberString);
                                        }
                                        else
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Part number: unknown.");
                                        }

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        if (FlashChipComboBox.SelectedIndex == 0)
                                        {
                                            CurrentTask = Task.DetectFlashMemoryType;
                                        }
                                        else
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 3. Detect flash memory type." + Environment.NewLine + Environment.NewLine + "Use selected flash chip.");

                                            if ((FlashFileBuffer != null) && (FlashFileBuffer.Length > 0) && (FlashFileBuffer.Length != FlashChipSize))
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Flash file size (" + FlashFileBuffer.Length.ToString() + " bytes) must be equal to the flash memory chip size (" + FlashChipSize.ToString() + " bytes)!");

                                                SCIBusBootstrapWorker.CancelAsync();
                                            }

                                            if (FlashMemoryBackupCheckBox.Checked)
                                            {
                                                CurrentTask = Task.BackupFlashMemory;
                                            }
                                            else
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 4. Backup flash memory." + Environment.NewLine + Environment.NewLine + "Skip flash memory backup.");

                                                if (EEPROMBackupCheckBox.Checked)
                                                {
                                                    CurrentTask = Task.BackupEEPROM;
                                                }
                                                else
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");

                                                    CurrentTask = Task.EraseFlashMemory;
                                                }
                                            }
                                        }

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashID:
                                    {
                                        if (SCIBusResponseBytes.Length < 3) break;

                                        byte mfgid = SCIBusResponseBytes[1];
                                        byte chipid = SCIBusResponseBytes[2];
                                        bool ManufacturerKnown = true;
                                        bool ChipTypeKnown = true;

                                        switch (mfgid)
                                        {
                                            case (byte)FlashMemoryManufacturer.STMicroelectronics:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory: ST ");
                                                break;
                                            }
                                            case (byte)FlashMemoryManufacturer.CATALYST:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory: CATALYST ");
                                                break;
                                            }
                                            case (byte)FlashMemoryManufacturer.Intel:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory: Intel ");
                                                break;
                                            }
                                            case (byte)FlashMemoryManufacturer.TexasInstruments:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory: Texas Instruments ");
                                                break;
                                            }
                                            default:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory: unknown ");
                                                ManufacturerKnown = false;
                                                break;
                                            }
                                        }

                                        if (ManufacturerKnown)
                                        {
                                            switch (chipid)
                                            {
                                                case (byte)FlashMemoryType.M28F102:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "M28F102 (128 kB).");
                                                    FlashChipComboBox.SelectedIndex = 1;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.CAT28F102:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "CAT28F102 (128 kB).");
                                                    FlashChipComboBox.SelectedIndex = 2;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.N28F010:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "N28F010 (128 kB).");
                                                    FlashChipComboBox.SelectedIndex = 3;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.N28F020:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "N28F020 (256 kB).");
                                                    FlashChipComboBox.SelectedIndex = 4;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.M28F210:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "M28F210 (256 kB).");
                                                    FlashChipComboBox.SelectedIndex = 5;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.M28F220:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "M28F220 (256 kB).");
                                                    FlashChipComboBox.SelectedIndex = 6;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.M28F200T:
                                                case (byte)FlashMemoryType.M28F200B:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "M28F200 (256 kB).");
                                                    FlashChipComboBox.SelectedIndex = 7;
                                                    break;
                                                }
                                                case (byte)FlashMemoryType.TMS28F210:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "TMS28F210 (256 kB).");
                                                    FlashChipComboBox.SelectedIndex = 9;
                                                    break;
                                                }
                                                default:
                                                {
                                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "(" + Util.ByteToHexString(SCIBusResponseBytes, 1, 2) + ").");
                                                    FlashChipComboBox.SelectedIndex = 0;
                                                    ChipTypeKnown = false;
                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "(" + Util.ByteToHexString(SCIBusResponseBytes, 1, 2) + ").");
                                            FlashChipComboBox.SelectedIndex = 0;
                                            ChipTypeKnown = false;
                                        }

                                        if (!ManufacturerKnown || !ChipTypeKnown)
                                        {
                                            FlashChipComboBox.SelectedIndex = 0;
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Result: " + Util.ByteToHexString(SCIBusResponseBytes, 1, SCIBusResponseBytes.Length - 1) + Environment.NewLine);
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory type could not be determined.");
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Consider selecting the correct chip by hand if issue persist.");
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Add request for flash memory chip support at:");
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "https://github.com/laszlodaniel/ChryslerScanner/discussions/8" + Environment.NewLine);

                                            if (SCIBusBootstrapWorker.IsBusy)
                                            {
                                                SCIBusBootstrapWorker.CancelAsync();
                                            }
                                        }

                                        if ((FlashFileBuffer != null) && (FlashFileBuffer.Length > 0) && (FlashFileBuffer.Length != FlashChipSize))
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash file size (" + FlashFileBuffer.Length.ToString() + " bytes) must be equal to the flash memory chip size (" + FlashChipSize.ToString() + " bytes)!");

                                            if (!SCIBusBootstrapWorker.IsBusy) break;

                                            SCIBusBootstrapWorker.CancelAsync();
                                        }
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start flash reading.");

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start flash erasing.");

                                        if (SCIBusResponseBytes.Length < 2) break;

                                        switch (SCIBusResponseBytes[1])
                                        {
                                            case (byte)SCI_ID.ExitWorkerFunction:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erased successfully.");
                                                FlashEraseSuccess = true;
                                                break;
                                            }
                                            case (byte)SCI_ID.EraseError_81:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error: 0x81.");
                                                FlashEraseSuccess = false;
                                                break;
                                            }
                                            case (byte)SCI_ID.EraseError_82:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error: 0x82.");
                                                FlashEraseSuccess = false;
                                                break;
                                            }
                                            case (byte)SCI_ID.EraseError_83:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error: 0x83.");
                                                FlashEraseSuccess = false;
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start flash writing.");

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.VerifyFlashChecksum:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Skip flash checksum verification.");

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        CurrentTask = Task.UpdateEEPROM;
                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start EEPROM reading.");

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start EEPROM writing.");

                                        if (CurrentTask == Task.UpdateEEPROM)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Skip EEPROM update.");
                                        }

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.Empty:
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start worker function.");
                                        SCIBusResponse = true;
                                        break;
                                    }
                                }
                                break;
                            }
                            case (byte)SCI_ID.ExitWorkerFunction:
                            {
                                switch ((byte)WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.FlashRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit flash reading.");

                                        if (CurrentTask == Task.BackupFlashMemory)
                                        {
                                            if (EEPROMBackupCheckBox.Checked)
                                            {
                                                switch (BootloaderComboBox.SelectedIndex)
                                                {
                                                    case (byte)Bootloader.JTEC:
                                                    case (byte)Bootloader.JTECPLUS_256k:
                                                    {
                                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");
                                                        CurrentTask = Task.EraseFlashMemory;
                                                        break;
                                                    }
                                                    default:
                                                    {
                                                        CurrentTask = Task.BackupEEPROM;
                                                        break;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 5. Backup EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM backup.");

                                                CurrentTask = Task.EraseFlashMemory;
                                            }
                                        }
                                        else if (CurrentTask == Task.ReadFlashMemory)
                                        {
                                            CurrentTask = Task.FinishFlashRead;
                                        }

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erased successfully.");
                                        FlashEraseSuccess = true;
                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.FlashWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit flash writing.");

                                        switch (BootloaderComboBox.SelectedIndex)
                                        {
                                            case (byte)Bootloader.JTEC:
                                            case (byte)Bootloader.JTECPLUS_256k:
                                            {
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 8. Verify flash checksum." + Environment.NewLine + Environment.NewLine + "Skip flash checksum verification.");
                                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Step 9. Update EEPROM." + Environment.NewLine + Environment.NewLine + "Skip EEPROM update.");
                                                CurrentTask = Task.FinishFlashWrite;
                                                break;
                                            }
                                            default:
                                            {
                                                CurrentTask = Task.VerifyFlashChecksum;
                                                break;
                                            }
                                        }

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.VerifyFlashChecksum:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit flash checksum verification.");

                                        switch (BootloaderComboBox.SelectedIndex)
                                        {
                                            case (byte)Bootloader.JTEC:
                                            case (byte)Bootloader.JTECPLUS_256k:
                                            {
                                                CurrentTask = Task.FinishFlashWrite;
                                                break;
                                            }
                                            default:
                                            {
                                                CurrentTask = Task.UpdateEEPROM;
                                                break;
                                            }
                                        }

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMRead:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit EEPROM reading.");

                                        if (CurrentTask == Task.BackupEEPROM)
                                        {
                                            CurrentTask = Task.EraseFlashMemory;
                                        }
                                        else if (CurrentTask == Task.ReadEEPROM)
                                        {
                                            CurrentTask = Task.FinishEEPROMRead;
                                        }

                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit EEPROM writing.");

                                        if (CurrentTask == Task.UpdateEEPROM)
                                        {
                                            CurrentTask = Task.FinishFlashWrite;
                                        }
                                        else if (CurrentTask == Task.WriteEEPROM)
                                        {
                                            CurrentTask = Task.FinishEEPROMWrite;
                                        }

                                        //SCIBusBootstrapFinished = true; // debug
                                        SCIBusResponse = true;
                                        break;
                                    }
                                    case (byte)WorkerFunction.Empty:
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit worker function.");
                                        SCIBusResponse = true;
                                        break;
                                    }
                                }
                                break;
                            }
                            case (byte)SCI_ID.BootstrapSeedKeyRequest:
                            {
                                break;
                            }
                            case (byte)SCI_ID.BootstrapSeedKeyResponse:
                            {
                                if (!((SCIBusResponseBytes.Length == 5) && (SCIBusResponseBytes[1] == 0xD0) && (SCIBusResponseBytes[2] == 0x67) && (SCIBusResponseBytes[3] == 0xC2) && (SCIBusResponseBytes[4] == 0x1F))) break;

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Unlock bootstrap mode security. OK.");
                                break;
                            }
                            case (byte)SCI_ID.FlashBlockWrite:
                            {
                                if (SCIBusResponseBytes.Length < 7) break;

                                List<byte> OffsetFBW = new List<byte>();
                                List<byte> LengthFBW = new List<byte>();
                                List<byte> ValuesFBW = new List<byte>();

                                OffsetFBW.AddRange(SCIBusResponseBytes.Skip(1).Take(3));
                                LengthFBW.AddRange(SCIBusResponseBytes.Skip(4).Take(2));
                                ValuesFBW.AddRange(SCIBusResponseBytes.Skip(6));

                                ushort BlockSizeFBW = (ushort)((SCIBusResponseBytes[4] << 8) + SCIBusResponseBytes[5]);
                                ushort EchoCountFBW = (ushort)(SCIBusResponseBytes.Length - 6);

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Write offset: " + Util.ByteToHexStringSimple(OffsetFBW.ToArray()) + ". Size: " + Util.ByteToHexStringSimple(LengthFBW.ToArray()) + ". ");

                                if (EchoCountFBW == BlockSizeFBW)
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "OK.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    if (!((SCIBusResponseBytes[1] == SCIBusTxPayload[1]) && (SCIBusResponseBytes[2] == SCIBusTxPayload[2]) && (SCIBusResponseBytes[3] == SCIBusTxPayload[3]))) break;

                                    SCIBusCurrentMemoryOffset += FlashWriteBlockSize;

                                    if ((FlashChipComboBox.SelectedIndex < 4) && (FlashChipComboBox.SelectedIndex != 0)) // 128 kB
                                    {
                                        if (SCIBusCurrentMemoryOffset >= 0x20000)
                                        {
                                            ExitButton_Click(this, EventArgs.Empty);
                                        }
                                    }
                                    else // 256 kB
                                    {
                                        if (SCIBusCurrentMemoryOffset >= 0x40000)
                                        {
                                            ExitButton_Click(this, EventArgs.Empty);
                                        }
                                    }

                                    SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";

                                    SCIBusResponse = true;
                                    SCIBusRxTimeoutTimer.Stop();
                                }
                                else
                                {
                                    switch (SCIBusResponseBytes[SCIBusResponseBytes.Length - 1]) // last payload byte stores error status
                                    {
                                        case (byte)SCI_ID.WriteError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Write error.");
                                            break;
                                        }
                                        case (byte)SCI_ID.BlockSizeError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid block size.");
                                            break;
                                        }
                                        default:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Unknown error.");
                                            break;
                                        }
                                    }

                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit flash writing.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    SCIBusBootstrapWorker.CancelAsync();
                                }
                                break;
                            }
                            case (byte)SCI_ID.FlashBlockRead:
                            {
                                if (SCIBusResponseBytes.Length < 7) break;

                                List<byte> OffsetFBR = new List<byte>();
                                List<byte> LengthFBR = new List<byte>();
                                List<byte> ValuesFBR = new List<byte>();

                                OffsetFBR.AddRange(SCIBusResponseBytes.Skip(1).Take(3));
                                LengthFBR.AddRange(SCIBusResponseBytes.Skip(4).Take(2));
                                ValuesFBR.AddRange(SCIBusResponseBytes.Skip(6));

                                ushort BlockSizeFBR = (ushort)((SCIBusResponseBytes[4] << 8) + SCIBusResponseBytes[5]);
                                ushort EchoCountFBR = (ushort)(SCIBusResponseBytes.Length - 6);

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Read offset: " + Util.ByteToHexStringSimple(OffsetFBR.ToArray()) + ". Size: " + Util.ByteToHexStringSimple(LengthFBR.ToArray()) + ". ");

                                if (EchoCountFBR == BlockSizeFBR)
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "OK.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    if (!((SCIBusResponseBytes[1] == SCIBusTxPayload[1]) && (SCIBusResponseBytes[2] == SCIBusTxPayload[2]) && (SCIBusResponseBytes[3] == SCIBusTxPayload[3]))) break;

                                    using (BinaryWriter writer = new BinaryWriter(File.Open(SCIBusFlashReadFilename, FileMode.Append)))
                                    {
                                        writer.Write(ValuesFBR.ToArray());
                                        writer.Close();
                                    }

                                    SCIBusCurrentMemoryOffset += FlashReadBlockSize;

                                    SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";

                                    if ((FlashChipComboBox.SelectedIndex < 4) && (FlashChipComboBox.SelectedIndex != 0)) // 128 kB
                                    {
                                        if (SCIBusCurrentMemoryOffset >= 0x20000)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory content saved to:" + Environment.NewLine + SCIBusFlashReadFilename);
                                            ExitButton_Click(this, EventArgs.Empty);
                                        }
                                    }
                                    else // 256 kB
                                    {
                                        if (SCIBusCurrentMemoryOffset >= 0x40000)
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash memory content saved to:" + Environment.NewLine + SCIBusFlashReadFilename);
                                            ExitButton_Click(this, EventArgs.Empty);
                                        }
                                    }

                                    SCIBusResponse = true;
                                    SCIBusRxTimeoutTimer.Stop();
                                }
                                else
                                {
                                    switch (SCIBusResponseBytes[SCIBusResponseBytes.Length - 1]) // last payload byte stores error status
                                    {
                                        case (byte)SCI_ID.BlockSizeError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid block size.");
                                            break;
                                        }
                                        default:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Unknown error.");
                                            break;
                                        }
                                    }

                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit flash reading.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    SCIBusBootstrapWorker.CancelAsync();
                                }
                                break;
                            }
                            case (byte)SCI_ID.EEPROMBlockWrite:
                            {
                                if (SCIBusResponseBytes.Length < 6) break;

                                List<byte> OffsetEBW = new List<byte>();
                                List<byte> LengthEBW = new List<byte>();
                                List<byte> ValuesEBW = new List<byte>();

                                OffsetEBW.AddRange(SCIBusResponseBytes.Skip(1).Take(2));
                                LengthEBW.AddRange(SCIBusResponseBytes.Skip(3).Take(2));
                                ValuesEBW.AddRange(SCIBusResponseBytes.Skip(5));

                                ushort BlockSizeEBW = (ushort)((SCIBusResponseBytes[3] << 8) + SCIBusResponseBytes[4]);
                                ushort EchoCountEBW = (ushort)(SCIBusResponseBytes.Length - 5);

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Write offset: " + Util.ByteToHexStringSimple(OffsetEBW.ToArray()) + ". Size: " + Util.ByteToHexStringSimple(LengthEBW.ToArray()) + ". ");

                                if ((EchoCountEBW == BlockSizeEBW) && (OffsetEBW[0] < 2))
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "OK.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    if (!((SCIBusResponseBytes[1] == SCIBusTxPayload[1]) && (SCIBusResponseBytes[2] == SCIBusTxPayload[2]))) break;

                                    SCIBusCurrentMemoryOffset += EEPROMWriteBlockSize;

                                    SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";

                                    ExitButton_Click(this, EventArgs.Empty);
                                    SCIBusResponse = true;
                                    SCIBusRxTimeoutTimer.Stop();
                                }
                                else
                                {
                                    switch (SCIBusResponseBytes[SCIBusResponseBytes.Length - 1]) // last payload byte stores error status
                                    {
                                        case (byte)SCI_ID.WriteError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Write error.");
                                            break;
                                        }
                                        case (byte)SCI_ID.BlockSizeError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid block size.");
                                            break;
                                        }
                                        case (byte)SCI_ID.OffsetError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid offset.");
                                            break;
                                        }
                                        default:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Unknown error.");
                                            break;
                                        }
                                    }

                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit EEPROM writing.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    SCIBusBootstrapWorker.CancelAsync();
                                }
                                break;
                            }
                            case (byte)SCI_ID.EEPROMBlockRead:
                            {
                                if (SCIBusResponseBytes.Length < 6) break;

                                List<byte> OffsetEBR = new List<byte>();
                                List<byte> LengthEBR = new List<byte>();
                                List<byte> ValuesEBR = new List<byte>();

                                OffsetEBR.AddRange(SCIBusResponseBytes.Skip(1).Take(2));
                                LengthEBR.AddRange(SCIBusResponseBytes.Skip(3).Take(2));
                                ValuesEBR.AddRange(SCIBusResponseBytes.Skip(5));

                                ushort BlockSizeEBR = (ushort)((SCIBusResponseBytes[3] << 8) + SCIBusResponseBytes[4]);
                                ushort EchoCountEBR = (ushort)(SCIBusResponseBytes.Length - 5);

                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Read offset: " + Util.ByteToHexStringSimple(OffsetEBR.ToArray()) + ". Size: " + Util.ByteToHexStringSimple(LengthEBR.ToArray()) + ". ");

                                if ((EchoCountEBR == BlockSizeEBR) && (OffsetEBR[0] < 2))
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "OK.");

                                    if (BlockSizeEBR == 512)
                                    {
                                        using (BinaryWriter writer = new BinaryWriter(File.Open(SCIBusEEPROMReadFilename, FileMode.Append)))
                                        {
                                            writer.Write(ValuesEBR.ToArray());
                                            writer.Close();
                                        }

                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "EEPROM content saved to:" + Environment.NewLine + SCIBusEEPROMReadFilename);

                                        if (!SCIBusBootstrapWorker.IsBusy) break;

                                        if (!((SCIBusResponseBytes[1] == SCIBusTxPayload[1]) && (SCIBusResponseBytes[2] == SCIBusTxPayload[2]))) break;

                                        SCIBusCurrentMemoryOffset += EEPROMReadBlockSize;

                                        SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";

                                        ExitButton_Click(this, EventArgs.Empty);
                                        SCIBusResponse = true;
                                        SCIBusRxTimeoutTimer.Stop();
                                    }
                                }
                                else
                                {
                                    switch (SCIBusResponseBytes[SCIBusResponseBytes.Length - 1]) // last payload byte stores error status
                                    {
                                        case (byte)SCI_ID.BlockSizeError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid block size.");
                                            break;
                                        }
                                        case (byte)SCI_ID.OffsetError:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Invalid offset.");
                                            break;
                                        }
                                        default:
                                        {
                                            UpdateTextBox(SCIBusBootstrapInfoTextBox, "Unknown error.");
                                            break;
                                        }
                                    }

                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Exit EEPROM reading.");

                                    if (!SCIBusBootstrapWorker.IsBusy) break;

                                    SCIBusBootstrapWorker.CancelAsync();
                                }
                                break;
                            }
                            case (byte)SCI_ID.StartBootloader:
                            {
                                if ((SCIBusResponseBytes.Length == 4) && (SCIBusResponseBytes[3] == 0x22))
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start bootloader. OK.");
                                }
                                else if (SCIBusResponseBytes.Length == 3)
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Start bootloader. Error.");
                                }
                                break;
                            }
                            case (byte)SCI_ID.UploadBootloader:
                            {
                                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Upload bootloader: ");

                                switch (BootloaderComboBox.SelectedIndex)
                                {
                                    case (byte)Bootloader.SBEC3_SBEC3PLUS_128k:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "SBEC3/SBEC3+ (128k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.SBEC3_128k_custom:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "SBEC3 (128k) custom. ");
                                        break;
                                    }
                                    case (byte)Bootloader.SBEC3A_3APLUS_3B_256k:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "SBEC3A/3A+/3B (256k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.SBEC3_256k_custom:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "SBEC3A (256k) custom. ");
                                        break;
                                    }
                                    case (byte)Bootloader.EATX3_128k:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "EATX3 (128k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.EATX3A_256k:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "EATX3A (256k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.JTEC:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "JTEC (256k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.JTECPLUS_256k:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "JTEC+ (256k). ");
                                        break;
                                    }
                                    case (byte)Bootloader.Empty:
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, "empty. ");
                                        break;
                                    }
                                }

                                ushort start = (ushort)((SCIBusResponseBytes[1] << 8) + SCIBusResponseBytes[2]);
                                ushort end = (ushort)((SCIBusResponseBytes[3] << 8) + SCIBusResponseBytes[4]);

                                if ((end - start + 1) == (SCIBusResponseBytes.Length - 5))
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "OK.");
                                }
                                else
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, "Error.");
                                }
                                break;
                            }
                            case (byte)SCI_ID.BlockSizeError:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.FlashRead:
                                    case (byte)WorkerFunction.FlashWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash block size error.");
                                        break;
                                    }
                                    case (byte)WorkerFunction.EEPROMRead:
                                    case (byte)WorkerFunction.EEPROMWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "EEPROM block size error.");
                                        break;
                                    }
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Block size error.");
                                        break;
                                    }
                                }

                                SCIBusBootstrapWorker.CancelAsync();
                                break;
                            }
                            case (byte)SCI_ID.EraseError_81:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error 0x81.");
                                        break;
                                    }
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Error 0x81.");
                                        break;
                                    }
                                }

                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                SCIBusBootstrapWorker.CancelAsync();
                                break;
                            }
                            case (byte)SCI_ID.EraseError_82:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error 0x82.");
                                        break;
                                    }
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Error 0x82.");
                                        break;
                                    }
                                }

                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                SCIBusBootstrapWorker.CancelAsync();
                                break;
                            }
                            case (byte)SCI_ID.EraseError_83:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.FlashErase:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Flash erase error 0x83.");
                                        break;
                                    }
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Error 0x83.");
                                        break;
                                    }
                                }

                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                SCIBusBootstrapWorker.CancelAsync();
                                break;
                            }
                            case (byte)SCI_ID.OffsetError:
                            {
                                switch (WorkerFunctionComboBox.SelectedIndex)
                                {
                                    case (byte)WorkerFunction.EEPROMRead:
                                    case (byte)WorkerFunction.EEPROMWrite:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "EEPROM offset error.");
                                        break;
                                    }
                                    default:
                                    {
                                        UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Error 0x84.");
                                        break;
                                    }
                                }

                                if (!SCIBusBootstrapWorker.IsBusy) break;

                                SCIBusBootstrapWorker.CancelAsync();
                                break;
                            }
                            case (byte)SCI_ID.BootstrapModeNotProtected:
                            {
                                if ((SCIBusResponseBytes.Length == 5) && (SCIBusResponseBytes[1] == 0x2F) && (SCIBusResponseBytes[2] == 0xD8) && (SCIBusResponseBytes[3] == 0x3E) && (SCIBusResponseBytes[4] == 0x23))
                                {
                                    UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + "Bootstrap mode is not protected.");
                                }
                                break;
                            }
                        }
                        break;
                    }
                }

                SCIBusNextRequest = false;
                SCIBusNextRequestTimer.Stop();
                SCIBusNextRequestTimer.Start();
            }, null);
        }

        private void PrepareFlashMemoryReading()
        {
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";
            SCIBusTxPayload = new byte[6];
            SCIBusTxPayload[0] = 0x33;
            SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 16) & 0xFF);
            SCIBusTxPayload[2] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
            SCIBusTxPayload[3] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);
            SCIBusTxPayload[4] = (byte)((FlashReadBlockSize >> 8) & 0xFF);
            SCIBusTxPayload[5] = (byte)(FlashReadBlockSize & 0xFF);
        }

        private void PrepareFlashMemoryWriting()
        {
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / (double)FlashChipSize * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/" + FlashChipSize.ToString() + " bytes)";
            SCIBusTxPayload = new byte[6 + FlashWriteBlockSize];
            SCIBusTxPayload[0] = 0x30;
            SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 16) & 0xFF);
            SCIBusTxPayload[2] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
            SCIBusTxPayload[3] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);
            SCIBusTxPayload[4] = (byte)((FlashWriteBlockSize >> 8) & 0xFF);
            SCIBusTxPayload[5] = (byte)(FlashWriteBlockSize & 0xFF);
            Array.Copy(FlashFileBuffer, SCIBusCurrentMemoryOffset, SCIBusTxPayload, 6, FlashWriteBlockSize);
        }

        private void PrepareEEPROMReading()
        {
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";
            SCIBusTxPayload = new byte[5] { 0x39, 0x00, 0x00, 0x02, 0x00 };
        }

        private void PrepareEEPROMWriting()
        {
            SCIBusCurrentMemoryOffset = 0;
            SCIBusBootstrapToolsProgressLabel.Text = "Progress: " + (byte)(Math.Round((double)SCIBusCurrentMemoryOffset / 512.0 * 100.0)) + "% (" + SCIBusCurrentMemoryOffset.ToString() + "/512 bytes)";
            SCIBusTxPayload = new byte[5 + EEPROMWriteBlockSize];
            SCIBusTxPayload[0] = 0x36;
            SCIBusTxPayload[1] = (byte)((SCIBusCurrentMemoryOffset >> 8) & 0xFF);
            SCIBusTxPayload[2] = (byte)(SCIBusCurrentMemoryOffset & 0xFF);
            SCIBusTxPayload[3] = (byte)((EEPROMWriteBlockSize >> 8) & 0xFF);
            SCIBusTxPayload[4] = (byte)(EEPROMWriteBlockSize & 0xFF);
            Array.Copy(EEPROMFileBuffer, SCIBusCurrentMemoryOffset, SCIBusTxPayload, 5, EEPROMWriteBlockSize);
        }

        private void UpdateTextBox(TextBox TB, string text)
        {
            if (TB.IsDisposed || !TB.IsHandleCreated)
                return;

            Invoke((MethodInvoker)delegate
            {
                if (TB.TextLength + text.Length > TB.MaxLength)
                {
                    TB.Clear();
                    GC.Collect();
                }

                TB.AppendText(text);

                if ((TB.Name == "SCIBusBootstrapInfoTextBox") && (SCIBusBootstrapInfoTextBox != null)) File.AppendAllText(SCIBusBootstrapLogFilename, text);
            });
        }

        private void BootstrapToolsForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (SCIBusBootstrapWorker.IsBusy) SCIBusBootstrapWorker.CancelAsync();

            if (SwitchBackToLSWhenExit && (OriginalForm.PCM.speed == "62500 baud"))
            {
                UpdateTextBox(SCIBusBootstrapInfoTextBox, Environment.NewLine + Environment.NewLine + "Scanner SCI-bus speed is set to 7812.5 baud.");
                OriginalForm.SelectSCIBusLSMode();
            }

            SerialService.PacketReceived -= PacketReceivedHandler; // unsubscribe from the PacketReceived event
        }
    }
}
