using System;
using System.Windows;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KeysightSD1;
using System.Windows.Forms;
using System.Windows.Input;

// 6/20/18 start working on scan control API for DE in slave mode

namespace ScanControl_slave
{
    public enum HW_STATUS_RETURNS
    {
        HW_SUCCESS,
        HW_OTHER
    }


    public class ScanControl_cz
    {
        private List<double> xpoints;
        private List<double> ypoints;
        private List<int> xindex;
        private List<int> yindex;


        public HW_STATUS_RETURNS ScanControlInitialize(double x_amp, double y_amp, double[] Xarray_vol, double[] Yarray_vol, int[] Xarray_index, int[] Yarray_index, double delay, int recording_rate, int Option2D)
        {
            int status;
            // Channel 1 for y scan and channel 2 for x scan

            //Create an instance of the AOU module
            SD_AOU moduleAOU = new SD_AOU();
            string ModuleName = "M3201A";
            int nChassis = 1;
            int nSlot = 7;
            int modelID;
            if ((modelID = moduleAOU.open(ModuleName, nChassis, nSlot)) < 0)
            {
                System.Windows.Forms.MessageBox.Show("The scan system is not successfully initialized. Restart the control box and then resstart computer. If still not solved, talk to Jingrui.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                Console.WriteLine("Error openning the Module 'M3201A', make sure the slot and chassis are correct. Aborting...");
                Console.ReadKey();

                return HW_STATUS_RETURNS.HW_SUCCESS;
            }

            // Determine prescaling factor and number of samples per step to use
            // according to Benjamin Bammels suggestion, use 5% to 10% longer frame time on AWG compared to DE frame integration time

            int nSamples;
            int Prescaling;

            nSamples = (int) Math.Ceiling(1.05e8 / recording_rate / 4095);
            nSamples = (nSamples / 5) * 5;
            Prescaling = (int) Math.Ceiling(1.05e8 / recording_rate / nSamples);
            while (Prescaling > 1.10e8/recording_rate/nSamples || nSamples == 0 || Prescaling  > 4095)
            {
                nSamples = nSamples+5;
                Prescaling = (int)Math.Ceiling(1.05e8 / recording_rate / nSamples);
            }

            int TriggerDelay;
            TriggerDelay = (int)Math.Floor(( 1e-8 * Prescaling * nSamples - 1 / recording_rate  ) * 1e9); // difference between scan cycle and camera integration time in ns
            if (TriggerDelay > 25000)
                TriggerDelay = 25000/10;
            else
                TriggerDelay = TriggerDelay / 10;

            // For global shutter mode, set trigger signal delay to 10000, move beam when reading data
            // 4/28/2021 check with chenyu about trigger delay time Benjamin suggested: 203.8us so  the value here should be 20380
            // from timing for DE-16 global shutter modes with high gain, no CDS
            TriggerDelay = 0;  

            Console.WriteLine("Precaling factor " + Prescaling + " will be used with " + nSamples + " for each beam position.");
            Console.WriteLine("Trigger delay by " + TriggerDelay*10 + " ns from beam position movement.");

            // Config amplitude and setup AWG in channels 1 and 2,
            moduleAOU.channelAmplitude(1, y_amp);
            moduleAOU.channelWaveShape(1, SD_Waveshapes.AOU_AWG);
            moduleAOU.channelAmplitude(2, x_amp);
            moduleAOU.channelWaveShape(2, SD_Waveshapes.AOU_AWG);
            moduleAOU.channelAmplitude(3, 0.5);
            moduleAOU.channelWaveShape(3, SD_Waveshapes.AOU_AWG);
            moduleAOU.channelAmplitude(4, 0.5);
            moduleAOU.channelWaveShape(4, SD_Waveshapes.AOU_AWG);
            moduleAOU.waveformFlush();

            // Convert array into list

            xpoints = new List<double>();
            ypoints = new List<double>();
            xindex = new List<int>();
            yindex = new List<int>();

            xpoints.Clear();
            ypoints.Clear();
            xindex.Clear();
            yindex.Clear();

            xpoints = Xarray_vol.ToList();
            ypoints = Yarray_vol.ToList();
            xindex = Xarray_index.ToList();
            yindex = Yarray_index.ToList();

            status = moduleAOU.AWGflush(1);
            Console.WriteLine("Status for channel 1 " + status);
            status = moduleAOU.AWGflush(2);
            Console.WriteLine("Status for channel 2 " + status);
            status = moduleAOU.AWGflush(3);
            Console.WriteLine("Status for channel 3 " + status);
            status = moduleAOU.AWGflush(4);
            Console.WriteLine("Status for channel 4 " + status);



            #region X scan generation

            // Generate and queue waveform for X channel on waveform #0 (channel 2) 

            var Waveform_X = new double[nSamples * xindex.Count()];
            int Count = 0;
            // create double array for each x cycle
            for (int ix = 0; ix < xindex.Count; ix++)
            {
                for (int i = 0; i < nSamples; i++)
                {
                    Waveform_X[Count] = xpoints[xindex[xindex.Count - ix - 1]];
                    Count++;
                }
            }

            //Check the size of waveform_x array 
            int length_x;
            length_x = Waveform_X.Length;
            Console.WriteLine("Array length of X waveform points is" + length_x);


            // generate SD_wave from array
            var SD_Waveform_X = new SD_Wave(SD_WaveformTypes.WAVE_ANALOG, Waveform_X);

            // load generated SD_wave to waveform #0
            status = moduleAOU.waveformLoad(SD_Waveform_X, 0, 1);       // padding option 1 is used to maintain ending voltage after each WaveForm
            if (status < 0)
            {
                Console.WriteLine("Error while loading x waveform");
            }
            //Console.WriteLine("X waveform size " + (double)moduleAOU.waveformGetMemorySize(0)/1000000 + " MB");

            // queue waveform into channel 2 and loop for yindex.count() times
            status = moduleAOU.AWGqueueWaveform(1, 0, SD_TriggerModes.AUTOTRIG, TriggerDelay, yindex.Count(), Prescaling); // triggerdelay in tens of ns

            if (status < 0)
            {
                Console.WriteLine("Error while queuing x waveform");
            }

            #endregion

            #region Y scan generation

            // Generate and queue waveform for Y channel on waveform #1 (channel 1)

            var Waveform_Y = new double[nSamples * xindex.Count() * yindex.Count()];
            Count = 0;
            for (int iy = 0; iy < yindex.Count(); iy++)
            {
                for (int ix = 0; ix < xindex.Count(); ix++)
                {
                    for (int i = 0; i < nSamples; i++)
                    {
                        Waveform_Y[Count] = ypoints[yindex[yindex.Count - iy - 1]];
                        Count++;
                    }
                }
            }

            //Check the size of waveform_y array
            int length_y;
            length_y = Waveform_Y.Length;
            Console.WriteLine("Array length of Y waveform points is" + length_y);




            var SD_Waveform_Y = new SD_Wave(SD_WaveformTypes.WAVE_ANALOG, Waveform_Y);
            status = moduleAOU.waveformLoad(SD_Waveform_Y, 1, 1);       // padding option 1 is used to maintain ending voltage after each WaveForm

            if (status < 0)
            {
                Console.WriteLine("Error while loading y waveform");
            }
            //Console.WriteLine("Y waveform size " + (double)moduleAOU.waveformGetMemorySize(1)/1000000 + " MB");


            // queue waveform into channel 1 and run once
            status = moduleAOU.AWGqueueWaveform(2, 1, SD_TriggerModes.AUTOTRIG, TriggerDelay, 1, Prescaling);

            if (status < 0)
            {
                Console.WriteLine("Error while queuing y waveform");
            }

            #endregion            

            #region generate DE trigger

            // Generate and queue waveform for DE trigger on wavefrom #2 (channel 3), same size and reps as x array

            var Waveform_DE = new double[nSamples * xindex.Count()];
            for (int ix = 0; ix < xindex.Count; ix++)
            {
                Waveform_DE[ix * nSamples] = -1;
            }

            // Add a line to save the array////////////////////////////////////////////////////
            //var csvarray = new StringBuilder();
            //int index = 0;
            //while (index < Waveform_DE.Count())
            //{
            //    csvarray.AppendLine(Waveform_DE[index].ToString());
            //}
            //string Path = "C:/Users/End_User/Desktop/WaveformDE_x100_y100_fps1000.csv";
            //File.WriteAllText(Path, csvarray.ToString());
            ///////////////////////////////////////////////////////////////////////////////
            var SD_Waveform_DE = new SD_Wave(SD_WaveformTypes.WAVE_ANALOG, Waveform_DE);
            status = moduleAOU.waveformLoad(SD_Waveform_DE, 2, 1);       // padding option 1 is used to maintain ending voltage after each WaveForm

            if (status < 0)
            {
                Console.WriteLine("Error while loading DE waveform");
            }

            status = moduleAOU.AWGqueueWaveform(3, 2, SD_TriggerModes.AUTOTRIG, 0, yindex.Count(), Prescaling);
            //Console.WriteLine("Trigger waveform size " + (double)moduleAOU.waveformGetMemorySize(2)/1000000 + " MB");

            if (status < 0)
            {
                Console.WriteLine("Error while queuing camera trigger, error code " + status);
            }

            #endregion

            #region generate digitizer trigger

            // Generate and queue waveform for digitizer trigger on waveform #3 (channel 4) 
            // trigger signal same size as x array, run only once

            var Waveform_DIGI = new double[nSamples * xindex.Count()];
            for (int ix = 0; ix < nSamples; ix++)
            {
                Waveform_DIGI[ix] = -1; // set first nSamples points to -1 to create on single trigger
            }
            var SD_Waveform_DIGI = new SD_Wave(SD_WaveformTypes.WAVE_ANALOG, Waveform_DIGI);
            status = moduleAOU.waveformLoad(SD_Waveform_DIGI, 3, 1);       // padding option 1 is used to maintain ending voltage after each WaveForm

            if (status < 0)
            {
                Console.WriteLine("Error while loading x waveform");
            }

            status = moduleAOU.AWGqueueWaveform(4, 3, SD_TriggerModes.AUTOTRIG, 0, 1, Prescaling);
            //Console.WriteLine("Trigger waveform size " + (double)moduleAOU.waveformGetMemorySize(3)/1000000 + " MB");

            if (status < 0)
            {
                Console.WriteLine("Error while queuing digitizer trigger, error code " + status);
            }

            #endregion

            // Configure all channels to single shot, X and trigger will automatically stop after certain amount of cycles
            moduleAOU.AWGqueueConfig(1, 0);
            moduleAOU.AWGqueueConfig(2, 0);
            moduleAOU.AWGqueueConfig(3, 0); 
            moduleAOU.AWGqueueConfig(4, 0);

            // Start both channel and wait for triggers, start channel 0,1,2: 00000111 = 7; start channel 0,1,2,3: 00001111 = 15
            System.Threading.Thread.Sleep(1000);
            if (Option2D == 0)
                moduleAOU.AWGstartMultiple(15);
            else
                moduleAOU.AWGstartMultiple(11); // don't start channel 3 for DE trigger if doing 2D scan mode
                                                //moduleAOU.AWGstartMultiple(3);

            //// Flush channels and delete waveform onboard when type esc
            //if (Console.KeyAvailable)
            //{
            //    ConsoleKeyInfo cki = Console.ReadKey(false);
            //    if (cki.Key == ConsoleKey.Escape)
            //    {
            //        moduleAOU.waveformFlush();
            //        moduleAOU.AWGstop(1);
            //        moduleAOU.AWGstop(2);
            //        moduleAOU.AWGstop(3);
            //        moduleAOU.AWGstop(4);
            //        moduleAOU.close();
            //        Console.WriteLine("AWG stopped and module closed.");
            //    }
            //}

            if ((status = moduleAOU.close()) < 0)
            {

                Console.WriteLine("Error closing the Module 'M3201A'");
                //Console.ReadKey();

                return HW_STATUS_RETURNS.HW_SUCCESS;
            }



            return HW_STATUS_RETURNS.HW_SUCCESS;

        }


    }
}